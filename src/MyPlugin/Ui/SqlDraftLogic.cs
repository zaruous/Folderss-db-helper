using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyPlugin
{
    /// <summary>임시 저장한 SQL 탭 하나.</summary>
    internal sealed class SqlDraft
    {
        /// <summary>대상 DB(접속 Id). 없으면 null.</summary>
        public string DbId { get; set; }

        public string Text { get; set; }

        public int Caret { get; set; }

        /// <summary>그 탭의 SQL 파일. 없으면 null.</summary>
        public string FilePath { get; set; }

        /// <summary>파일 인코딩 이름(SqlFileEncoding). 없으면 null.</summary>
        public string FileEncoding { get; set; }

        /// <summary>파일의 줄바꿈. 없으면 null.</summary>
        public string Newline { get; set; }

        /// <summary>파일 탭이 저장하지 않은 변경을 가졌음. false면 되살릴 때 파일을 다시 읽는다(그 사이 바뀌었을 수 있음).</summary>
        public bool Dirty { get; set; }
    }

    /// <summary>DB Helper 창 하나의 임시 저장 내용.</summary>
    internal sealed class SqlDraftSet
    {
        public int Version { get; set; } = 1;

        public DateTime SavedAtUtc { get; set; }

        /// <summary>Tabs 안에서 보던 탭의 위치. 없으면 -1.</summary>
        public int Active { get; set; } = -1;

        public List<SqlDraft> Tabs { get; set; } = new List<SqlDraft>();
    }

    /// <summary>임시 저장 파일 내용을 잠그고 푼다. 풀지 못하면 예외(CryptographicException 등).</summary>
    internal interface ISqlDraftProtector
    {
        byte[] Protect(byte[] data);

        byte[] Unprotect(byte[] data);
    }

    /// <summary>Windows 사용자 범위 DPAPI — 같은 PC의 같은 Windows 사용자만 푼다(접속 비밀번호와 같은 방식).</summary>
    internal sealed class DpapiDraftProtector : ISqlDraftProtector
    {
        public static readonly DpapiDraftProtector Instance = new DpapiDraftProtector();

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MyPlugin.SqlDraft.v1");

        public byte[] Protect(byte[] data)
        {
            return ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        }

        public byte[] Unprotect(byte[] data)
        {
            return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
        }
    }

    /// <summary>읽지 못해 보관 폴더로 옮긴(옮기지 못했으면 그 자리의) 임시 저장 파일.</summary>
    internal sealed class SqlDraftBroken
    {
        public string Path { get; set; }

        public string Reason { get; set; }
    }

    /// <summary>넘겨받을 임시 저장(<see cref="SqlDraftLogic.LoadOrphans(string, ICollection{string}, ISqlDraftProtector)"/>).</summary>
    internal sealed class SqlDraftLoad
    {
        /// <summary>파일 경로와 내용 — 저장 시각 순(오래된 것 먼저).</summary>
        public List<KeyValuePair<string, SqlDraftSet>> Orphans { get; } = new List<KeyValuePair<string, SqlDraftSet>>();

        public List<SqlDraftBroken> Broken { get; } = new List<SqlDraftBroken>();
    }

    /// <summary>
    /// SQL 임시 저장(창을 닫았다 열어도 쓰던 SQL 탭이 남게). 순수 로직과 파일 다루기라 시험 대상이다.
    /// - DB Helper 창마다 자기 파일(폴더/창 Id.dat)에 쓴다. 같은 프로세스에 창이 여럿 열려도 서로 덮어쓰지 않게.
    /// - 내용(JSON)은 Windows 사용자 범위 DPAPI로 잠근다(머리 4바이트 "FSD1" + 잠근 바이트). 파일에 SQL·경로가 평문으로 남지 않는다.
    /// - 새 창은 지금 열린 창의 것이 아닌 파일(닫힌 창·비정상 종료가 남긴 것)을 넘겨받아 탭으로 되살린다.
    ///   넘겨받은 내용을 자기 파일에 저장한 뒤에 원래 파일을 지운다(그 사이 끊겨도 잃지 않음).
    ///   이전 버전의 평문 파일(*.json)도 같은 길로 넘겨받는다 — 잠가 다시 저장한 뒤 평문을 지운다.
    /// - 쓰기는 잠근 내용을 풀어 확인하고 임시 파일에 쓴 뒤 바꿔치기한다(잠그기·쓰기가 실패해도 이전 내용이 남음).
    /// - 풀지 못하는 파일(다른 Windows 사용자·PC에서 저장, 손상)은 지우지 않고 보관 폴더(broken)로 옮겨 알린다.
    ///   다른 프로그램이 잡고 있어 읽지 못한 파일은 그대로 둔다(다음에 다시 넘겨받음).
    /// </summary>
    internal static class SqlDraftLogic
    {
        public const string FolderName = "sql-drafts";

        /// <summary>풀지 못한 파일을 옮겨 두는 하위 폴더.</summary>
        public const string BrokenFolderName = "broken";

        /// <summary>입력이 멈춘 뒤 저장할 때까지 기다리는 시간.</summary>
        public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(1500);

        private const string Extension = ".dat";
        private const string LegacyExtension = ".json";
        private const string TempSuffix = ".tmp";
        private static readonly byte[] Magic = { (byte)'F', (byte)'S', (byte)'D', (byte)'1' };

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>저장할 탭인지: 공백뿐이거나 탭을 만들 때의 글(첫 탭 안내문·새 탭 머리말)과 같으면 저장하지 않는다. 줄바꿈 형식·앞뒤 공백은 무시.</summary>
        public static bool IsWorthSaving(string text, string initialText)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return !string.Equals(Normalize(text), Normalize(initialText), StringComparison.Ordinal);
        }

        public static string FolderOf(string dataDirectory)
        {
            return Path.Combine(dataDirectory, FolderName);
        }

        public static string FileOf(string folder, string ownerId)
        {
            return Path.Combine(folder, ownerId + Extension);
        }

        /// <summary>잠근 파일 내용: 머리(FSD1) + 잠근 JSON(UTF-8).</summary>
        public static byte[] Encode(SqlDraftSet set, ISqlDraftProtector protector)
        {
            var data = protector.Protect(Encoding.UTF8.GetBytes(Serialize(set)));
            var bytes = new byte[Magic.Length + data.Length];
            Buffer.BlockCopy(Magic, 0, bytes, 0, Magic.Length);
            Buffer.BlockCopy(data, 0, bytes, Magic.Length, data.Length);
            return bytes;
        }

        /// <summary>잠근 파일 내용을 푼다. 머리가 다르면 InvalidDataException, 풀지 못하면 CryptographicException, 내용이 깨졌으면 null.</summary>
        public static SqlDraftSet Decode(byte[] bytes, ISqlDraftProtector protector)
        {
            if (bytes == null || bytes.Length < Magic.Length || !bytes.Take(Magic.Length).SequenceEqual(Magic))
                throw new InvalidDataException("임시 저장 파일 형식이 아닙니다.");
            var data = new byte[bytes.Length - Magic.Length];
            Buffer.BlockCopy(bytes, Magic.Length, data, 0, data.Length);
            return Deserialize(Encoding.UTF8.GetString(protector.Unprotect(data)));
        }

        /// <summary>임시 저장에서 뺀 접속(대상 DB Id)의 탭을 뺀 묶음. 보던 탭이 빠지면 -1.</summary>
        public static SqlDraftSet WithoutExcluded(SqlDraftSet set, ICollection<string> excludedDbIds)
        {
            if (set == null || excludedDbIds == null || excludedDbIds.Count == 0)
                return set;
            var kept = new SqlDraftSet { Version = set.Version, SavedAtUtc = set.SavedAtUtc };
            var tabs = set.Tabs ?? new List<SqlDraft>();
            for (var i = 0; i < tabs.Count; i++)
            {
                if (tabs[i] == null || (tabs[i].DbId != null && excludedDbIds.Contains(tabs[i].DbId)))
                    continue;
                if (i == set.Active)
                    kept.Active = kept.Tabs.Count;
                kept.Tabs.Add(tabs[i]);
            }
            return kept;
        }

        public static string Serialize(SqlDraftSet set)
        {
            return JsonSerializer.Serialize(set, JsonOptions);
        }

        /// <summary>읽지 못하면(깨진 파일·다른 형식) null. 빈 탭은 뺀다.</summary>
        public static SqlDraftSet Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var set = JsonSerializer.Deserialize<SqlDraftSet>(json, JsonOptions);
                if (set == null)
                    return null;
                var tabs = set.Tabs ?? new List<SqlDraft>();
                var kept = new List<SqlDraft>();
                var active = -1;
                for (var i = 0; i < tabs.Count; i++)
                {
                    if (tabs[i] == null || string.IsNullOrWhiteSpace(tabs[i].Text))
                        continue;
                    if (i == set.Active)
                        active = kept.Count;
                    kept.Add(tabs[i]);
                }
                set.Tabs = kept;
                set.Active = active;
                return set;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>창 하나의 탭들을 DPAPI로 잠가 저장한다.</summary>
        public static void Save(string folder, string ownerId, SqlDraftSet set)
        {
            Save(folder, ownerId, set, DpapiDraftProtector.Instance);
        }

        /// <summary>
        /// 창 하나의 탭들을 잠가 저장한다. 저장할 탭이 없으면 그 창의 파일을 지운다. 실패하면 예외(IOException·CryptographicException 등).
        /// 잠근 내용을 다시 풀어 탭 수가 맞는지 본 뒤 임시 파일에 쓰고 바꿔치기한다 — 어느 단계에서 실패해도 이전 파일은 그대로다.
        /// </summary>
        public static void Save(string folder, string ownerId, SqlDraftSet set, ISqlDraftProtector protector)
        {
            var path = FileOf(folder, ownerId);
            if (set == null || set.Tabs == null || set.Tabs.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            var bytes = Encode(set, protector);
            var check = Decode(bytes, protector);
            if (check == null || check.Tabs.Count != set.Tabs.Count(t => t != null && !string.IsNullOrWhiteSpace(t.Text)))
                throw new InvalidDataException("잠근 임시 저장 내용을 다시 풀어 확인하지 못했습니다.");
            Directory.CreateDirectory(folder);
            var temp = path + TempSuffix;
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, true);
        }

        public static SqlDraftLoad LoadOrphans(string folder, ICollection<string> liveOwners)
        {
            return LoadOrphans(folder, liveOwners, DpapiDraftProtector.Instance);
        }

        /// <summary>
        /// 넘겨받을 파일들: 폴더의 *.dat(잠근 것)·*.json(이전 버전 평문) 중 지금 열린 창(liveOwners)의 것이 아닌 것을 저장 시각 순(오래된 것 먼저)으로.
        /// 다른 프로그램이 잡고 있어 읽지 못한 파일은 빼고 그대로 둔다. 탭이 없는 파일도 돌려준다(지우라고).
        /// 풀지 못한·깨진 파일은 보관 폴더로 옮겨 Broken에 담는다(지우지 않음 — 저장한 사용자로 열거나 직접 확인할 수 있게).
        /// </summary>
        public static SqlDraftLoad LoadOrphans(string folder, ICollection<string> liveOwners, ISqlDraftProtector protector)
        {
            var load = new SqlDraftLoad();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return load;
            var found = new List<KeyValuePair<string, SqlDraftSet>>();
            foreach (var path in Directory.GetFiles(folder))
            {
                var legacy = path.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase);
                if (!legacy && !path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                    continue;
                var owner = Path.GetFileNameWithoutExtension(path);
                if (liveOwners != null && liveOwners.Contains(owner))
                    continue;
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                SqlDraftSet set;
                string reason = null;
                try
                {
                    set = legacy ? Deserialize(Encoding.UTF8.GetString(bytes)) : Decode(bytes, protector);
                }
                catch (CryptographicException)
                {
                    set = null;
                    reason = "다른 Windows 사용자나 다른 PC에서 저장했거나 파일이 손상되어 풀 수 없음";
                }
                catch (InvalidDataException)
                {
                    set = null;
                    reason = "임시 저장 파일 형식이 아님";
                }
                if (set != null)
                {
                    found.Add(new KeyValuePair<string, SqlDraftSet>(path, set));
                    continue;
                }
                load.Broken.Add(new SqlDraftBroken { Path = MoveToBroken(folder, path), Reason = reason ?? "내용이 깨져 읽을 수 없음" });
            }
            load.Orphans.AddRange(found.OrderBy(p => p.Value.SavedAtUtc).ThenBy(p => p.Key, StringComparer.Ordinal));
            return load;
        }

        /// <summary>보관 폴더(broken)로 옮긴 경로. 옮기지 못하면 원래 경로(그대로 둠 — 다음에 다시 알림).</summary>
        private static string MoveToBroken(string folder, string path)
        {
            try
            {
                var broken = Path.Combine(folder, BrokenFolderName);
                Directory.CreateDirectory(broken);
                var name = Path.GetFileNameWithoutExtension(path) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var target = Path.Combine(broken, name + Path.GetExtension(path));
                for (var i = 2; File.Exists(target); i++)
                    target = Path.Combine(broken, name + "-" + i.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(path));
                File.Move(path, target);
                return target;
            }
            catch (Exception)
            {
                return path;
            }
        }

        /// <summary>임시 저장 폴더의 저장 파일(잠근 것·이전 평문·쓰다 남은 임시 파일·보관 폴더)을 모두 지운다. 지운 파일 수. 지우지 못하면 예외.</summary>
        public static int Clear(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return 0;
            var count = 0;
            foreach (var path in Directory.GetFiles(folder))
            {
                if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && !path.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase)
                    && !path.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Delete(path);
                count++;
            }
            var broken = Path.Combine(folder, BrokenFolderName);
            if (Directory.Exists(broken))
            {
                count += Directory.GetFiles(broken, "*", SearchOption.AllDirectories).Length;
                Directory.Delete(broken, true);
            }
            return count;
        }

        /// <summary>넘겨받은 묶음들을 하나로(오래된 창 먼저, 창 안의 탭 순서 유지). 보던 탭은 가장 최근 묶음의 것.</summary>
        public static SqlDraftSet Merge(IEnumerable<SqlDraftSet> sets)
        {
            var merged = new SqlDraftSet();
            foreach (var set in sets ?? Enumerable.Empty<SqlDraftSet>())
            {
                if (set == null || set.Tabs == null || set.Tabs.Count == 0)
                    continue;
                var offset = merged.Tabs.Count;
                merged.Tabs.AddRange(set.Tabs);
                if (set.Active >= 0 && set.Active < set.Tabs.Count)
                    merged.Active = offset + set.Active;
                if (set.SavedAtUtc > merged.SavedAtUtc)
                    merged.SavedAtUtc = set.SavedAtUtc;
            }
            return merged;
        }

        /// <summary>파일들을 지운다. 지우지 못한 것은 그대로 둔다(다음에 다시 넘겨받음).</summary>
        public static void DeleteQuietly(IEnumerable<string> paths)
        {
            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception)
                {
                    // 다른 창·프로그램이 잡고 있으면 남겨 둔다 — 내용은 이미 이 창 파일에도 있다
                }
            }
        }

        public static string RestoredMessage(int count)
        {
            return "임시 저장한 SQL 탭 " + count.ToString(CultureInfo.InvariantCulture) + "개를 되살렸습니다.";
        }

        public static string SaveFailedMessage(string reason)
        {
            return "SQL을 임시 저장하지 못했습니다: " + reason;
        }

        public static string LoadFailedMessage(string reason)
        {
            return "임시 저장한 SQL을 읽지 못했습니다: " + reason;
        }

        public static string BrokenMessage(SqlDraftBroken broken)
        {
            return "임시 저장한 SQL 파일 하나를 되살리지 못했습니다(" + broken.Reason + "). 지우지 않고 보관했습니다: " + broken.Path
                + " — 저장한 Windows 사용자로 열면 되살릴 수 있으며, 필요 없으면 설정 › SQL 편집기 › [임시 저장 지우기]로 지웁니다.";
        }

        public static string ClearedMessage(int count)
        {
            return "임시 저장 파일 " + count.ToString(CultureInfo.InvariantCulture) + "개를 지웠습니다. 열려 있는 DB Helper 창의 탭은 다음 자동 저장 때 다시 저장됩니다.";
        }

        private static string Normalize(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }
    }
}
