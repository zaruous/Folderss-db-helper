using System;
using System.IO;

namespace MyPlugin
{
    /// <summary>파일의 수정 시각(UTC)·크기. 둘 다 같으면 같은 파일 내용으로 본다. 값이 같으면 같다(record).</summary>
    internal sealed record SqlFileStamp(DateTime WriteUtc, long Length)
    {
        /// <summary>파일이 없음(지워졌거나 이름이 바뀌었거나 접근할 수 없음).</summary>
        public static readonly SqlFileStamp Missing = new SqlFileStamp(DateTime.MinValue, -1);

        public bool IsMissing
        {
            get { return Length < 0; }
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
        /// <summary>지금 파일 상태. 파일이 없으면 Missing, 경로가 비었거나 읽지 못하면(경로 형식 오류 등) null(모름).</summary>
        public static SqlFileStamp Of(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new SqlFileStamp(info.LastWriteTimeUtc, info.Length) : SqlFileStamp.Missing;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>기준(known)과 지금(current)을 비교한다. 어느 쪽이든 모르면(null) None — 모르는 것으로 묻지 않는다.</summary>
        public static FileChange Detect(SqlFileStamp known, SqlFileStamp current, bool dirty)
        {
            if (known == null || current == null || known == current)
                return FileChange.None;
            if (current.IsMissing)
                return FileChange.Missing;
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
    }
}
