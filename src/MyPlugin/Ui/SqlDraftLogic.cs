using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// SQL 임시 저장(창을 닫았다 열어도 쓰던 SQL 탭이 남게). 순수 로직과 파일 다루기라 시험 대상이다.
    /// - DB Helper 창마다 자기 파일(폴더/창 Id.json)에 쓴다. 같은 프로세스에 창이 여럿 열려도 서로 덮어쓰지 않게.
    /// - 새 창은 지금 열린 창의 것이 아닌 파일(닫힌 창·비정상 종료가 남긴 것)을 넘겨받아 탭으로 되살린다.
    ///   넘겨받은 내용을 자기 파일에 저장한 뒤에 원래 파일을 지운다(그 사이 끊겨도 잃지 않음).
    /// - 쓰기는 임시 파일에 쓴 뒤 바꿔치기한다(쓰다 끊겨도 이전 내용이 남음). 읽지 못하는 파일은 건너뛰고 지우지 않는다.
    /// </summary>
    internal static class SqlDraftLogic
    {
        public const string FolderName = "sql-drafts";

        /// <summary>입력이 멈춘 뒤 저장할 때까지 기다리는 시간.</summary>
        public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(1500);

        private const string Extension = ".json";
        private const string TempSuffix = ".tmp";

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

        /// <summary>창 하나의 탭들을 저장한다. 저장할 탭이 없으면 그 창의 파일을 지운다. 실패하면 예외(IOException 등).</summary>
        public static void Save(string folder, string ownerId, SqlDraftSet set)
        {
            var path = FileOf(folder, ownerId);
            if (set == null || set.Tabs == null || set.Tabs.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            Directory.CreateDirectory(folder);
            var temp = path + TempSuffix;
            File.WriteAllText(temp, Serialize(set), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }

        /// <summary>
        /// 넘겨받을 파일들: 폴더의 *.json 중 지금 열린 창(liveOwners)의 것이 아닌 것을 저장 시각 순(오래된 것 먼저)으로.
        /// 읽지 못한 파일(깨짐·다른 프로그램이 잡고 있음)은 빼고 그대로 둔다. 탭이 없는 파일도 돌려준다(지우라고).
        /// </summary>
        public static List<KeyValuePair<string, SqlDraftSet>> LoadOrphans(string folder, ICollection<string> liveOwners)
        {
            var result = new List<KeyValuePair<string, SqlDraftSet>>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return result;
            foreach (var path in Directory.GetFiles(folder, "*" + Extension))
            {
                if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                    continue;
                var owner = Path.GetFileNameWithoutExtension(path);
                if (liveOwners != null && liveOwners.Contains(owner))
                    continue;
                SqlDraftSet set;
                try
                {
                    set = Deserialize(File.ReadAllText(path, Encoding.UTF8));
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                if (set != null)
                    result.Add(new KeyValuePair<string, SqlDraftSet>(path, set));
            }
            return result.OrderBy(p => p.Value.SavedAtUtc).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
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

        private static string Normalize(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }
    }
}
