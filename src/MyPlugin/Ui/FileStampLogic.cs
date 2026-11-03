using System;
using System.Collections.Generic;
using System.IO;

namespace MyPlugin
{
    /// <summary>파일의 수정 시각(UTC)·크기. 둘 다 같으면 같은 파일 내용으로 본다. 값이 같으면 같다(record).</summary>
    internal sealed record SqlFileStamp(DateTime WriteUtc, long Length)
    {
        /// <summary>파일이 없음(드라이브는 닿는데 파일이 없음 — 지워졌거나 이름이 바뀜).</summary>
        public static readonly SqlFileStamp Missing = new SqlFileStamp(DateTime.MinValue, -1);

        /// <summary>드라이브(Z:\ · \\server\share)에 닿지 않음(네트워크 끊김 등). 기준으로 삼지 않는다.</summary>
        public static readonly SqlFileStamp Unreachable = new SqlFileStamp(DateTime.MinValue, -2);

        public bool IsMissing
        {
            get { return Length == -1; }
        }

        public bool IsUnreachable
        {
            get { return Length == -2; }
        }
    }

    /// <summary>밖에서 바뀐 파일에 대한 처리.</summary>
    internal enum FileChange
    {
        /// <summary>그대로(또는 판단할 수 없음).</summary>
        None,
        /// <summary>바뀜 — 탭에 저장하지 않은 변경이 없으니 다시 읽으면 된다.</summary>
        Reload,
        /// <summary>바뀜 — 탭에도 저장하지 않은 변경이 있다(사용자에게 묻는다).</summary>
        Conflict,
        /// <summary>기준을 몰랐고 탭에 저장하지 않은 변경이 있다 — 묻지 않고 지금 상태를 기준으로 잡기만 한다.</summary>
        Adopt,
        /// <summary>파일이 없어졌다.</summary>
        Missing
    }

    /// <summary>
    /// 파일 탭이 밖에서 바뀐 파일을 알아채는 판단(순수 로직 + 파일 정보 읽기, 시험 대상).
    /// 감시(FileSystemWatcher) 없이 탭에 포커스가 올 때·저장 직전에만 기준(탭이 아는 수정 시각·크기)과 지금 상태를 비교한다.
    /// 한 번 알린 변경은 호출하는 쪽이 기준을 지금 상태로 옮겨 다시 묻지 않는다.
    /// </summary>
    internal static class FileStampLogic
    {
        /// <summary>
        /// 지금 파일 상태. 파일이 없을 때 드라이브 루트도 없으면 Unreachable(끊긴 네트워크 드라이브가 바로 "없음"으로 답하는 경우), 아니면 Missing.
        /// 경로가 비었거나 읽지 못하면(경로 형식 오류 등) null(모름). directoryExists는 시험에서 바꾼다.
        /// </summary>
        public static SqlFileStamp Of(string path, Func<string, bool> directoryExists = null)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                    return new SqlFileStamp(info.LastWriteTimeUtc, info.Length);
                var root = Path.GetPathRoot(info.FullName);
                return !string.IsNullOrEmpty(root) && !(directoryExists ?? Directory.Exists)(root) ? SqlFileStamp.Unreachable : SqlFileStamp.Missing;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>
        /// 기준(known)과 지금(current)을 비교한다. 지금 상태를 모르거나 접근할 수 없으면 None(묻지 않고 기준도 그대로).
        /// 기준을 모르면(null·접근 불가 — 이전 버전 임시 저장, 파일을 읽지 않고 되살린 탭) 고친 탭은 Adopt, 안 고친 탭은 Reload.
        /// </summary>
        public static FileChange Detect(SqlFileStamp known, SqlFileStamp current, bool dirty)
        {
            if (current == null || current.IsUnreachable)
                return FileChange.None;
            if (current.IsMissing)
                return known != null && known.IsMissing ? FileChange.None : FileChange.Missing;
            if (known == null || known.IsUnreachable)
                return dirty ? FileChange.Adopt : FileChange.Reload;
            if (known == current)
                return FileChange.None;
            return dirty ? FileChange.Conflict : FileChange.Reload;
        }

        /// <summary>임시 저장 값 → 기준. 둘 중 하나라도 없으면(이전 버전 임시 저장) null.</summary>
        public static SqlFileStamp FromDraft(DateTime? writeUtc, long? length)
        {
            return writeUtc.HasValue && length.HasValue ? new SqlFileStamp(writeUtc.Value, length.Value) : null;
        }

        // ---------- 문장 ----------

        public static string ConflictQuestion(string path)
        {
            return "이 탭에서 연 뒤 다른 곳에서 파일이 바뀌었습니다." + Environment.NewLine + path + Environment.NewLine + Environment.NewLine
                + "이 탭에도 저장하지 않은 변경이 있습니다. 파일을 다시 읽을까요?" + Environment.NewLine
                + "[다시 읽기]: 탭 내용을 파일 내용으로 바꿉니다(Ctrl+Z로 되돌릴 수 있음)." + Environment.NewLine
                + "[취소]: 탭 내용을 그대로 둡니다. 저장하면 파일을 덮어씁니다.";
        }

        public static string OverwriteQuestion(string path)
        {
            return "이 탭에서 연 뒤 다른 곳에서 파일이 바뀌었습니다." + Environment.NewLine + path + Environment.NewLine + Environment.NewLine
                + "저장하면 다른 곳에서 바꾼 내용을 덮어씁니다. 덮어쓸까요?";
        }

        public static string MissingNotice(string path)
        {
            return "파일을 찾을 수 없습니다(지워졌거나 이름이 바뀌었거나 접근할 수 없음)." + Environment.NewLine + path + Environment.NewLine + Environment.NewLine
                + "탭 내용은 그대로 있습니다. 저장하면 이 경로에 다시 만듭니다.";
        }

        public static string ReloadedMessage(string path)
        {
            return "다른 곳에서 바뀐 파일을 다시 읽었습니다: " + path;
        }

        public static string CheckStoppedMessage(string root)
        {
            return root + " 이(가) " + DriveCheckCounter.MaxFailures + "번 연속 응답하지 않거나 접근할 수 없어 이 창을 다시 열 때까지 바뀐 파일 확인을 멈춥니다"
                + "(그 드라이브의 파일을 열거나 저장하면 다시 확인합니다).";
        }
    }

    /// <summary>
    /// 드라이브(Z:\ · \\server\share)마다 파일 상태 읽기가 제한 시간 안에 끝나지 않았거나 드라이브에 닿지 않은 횟수. MaxFailures번 연속이면 포커스 확인을 멈춘다.
    /// 그 드라이브에서 무엇이든 성공하면(확인·열기·저장·다시 읽기) 되돌린다. 창마다 하나 — 창을 다시 열면 처음부터 센다.
    /// </summary>
    internal sealed class DriveCheckCounter
    {
        public const int MaxFailures = 3;

        private readonly Dictionary<string, int> _failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>경로의 드라이브: "Z:\" 또는 "\\server\share"(UNC). Windows 경로를 글자로만 본다(시험이 Linux에서도 돌게). 모르면 "".</summary>
        public static string RootOf(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "";
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var parts = path.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? @"\\" + parts[0] + @"\" + parts[1] : parts.Length == 1 ? @"\\" + parts[0] : "";
            }
            if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
                return char.ToUpperInvariant(path[0]) + @":\";
            return "";
        }

        public bool IsStopped(string path)
        {
            int count;
            var root = RootOf(path);
            return root.Length > 0 && _failures.TryGetValue(root, out count) && count >= MaxFailures;
        }

        /// <summary>응답 없음 한 번. 이번에 막 멈췄으면 true(그때 한 번만 알린다).</summary>
        public bool Fail(string path)
        {
            var root = RootOf(path);
            if (root.Length == 0)
                return false;
            int count;
            _failures.TryGetValue(root, out count);
            _failures[root] = count + 1;
            return count + 1 == MaxFailures;
        }

        public void Succeed(string path)
        {
            _failures.Remove(RootOf(path));
        }
    }
}
