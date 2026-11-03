using System;
using System.IO;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>FileStampLogic: 파일 수정 시각·크기 읽기와 밖에서 바뀐 파일 판단(탭 포커스·저장 직전 확인).</summary>
    public class FileStampLogicTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "file-stamp-test-" + Guid.NewGuid().ToString("N"));

        public FileStampLogicTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private static readonly SqlFileStamp Known = new SqlFileStamp(new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc), 100);

        [Fact]
        public void Of_ReadsWriteTimeAndLength()
        {
            var path = Path.Combine(_folder, "a.sql");
            File.WriteAllText(path, "select 1");
            var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, when);

            var stamp = FileStampLogic.Of(path);

            Assert.Equal(new SqlFileStamp(when, 8), stamp);
            Assert.False(stamp.IsMissing);
        }

        [Fact]
        public void Of_MissingFile_IsMissing()
        {
            var stamp = FileStampLogic.Of(Path.Combine(_folder, "none.sql"));

            Assert.True(stamp.IsMissing);
            Assert.Equal(SqlFileStamp.Missing, stamp);
        }

        [Fact]
        public void Of_EmptyPath_IsUnknown()
        {
            Assert.Null(FileStampLogic.Of(null));
            Assert.Null(FileStampLogic.Of(""));
        }

        /// <summary>재현: 연 뒤 다른 프로그램이 같은 크기로 고쳐도(시각만 바뀜) 알아챈다.</summary>
        [Fact]
        public void ExternalEdit_SameLength_IsDetected()
        {
            var path = Path.Combine(_folder, "b.sql");
            File.WriteAllText(path, "select 1");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var known = FileStampLogic.Of(path);

            File.WriteAllText(path, "select 2");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));

            Assert.Equal(FileChange.Reload, FileStampLogic.Detect(known, FileStampLogic.Of(path), false));
            Assert.Equal(FileChange.Conflict, FileStampLogic.Detect(known, FileStampLogic.Of(path), true));
        }

        [Fact]
        public void Detect_SameStamp_IsNone()
        {
            Assert.Equal(FileChange.None, FileStampLogic.Detect(Known, new SqlFileStamp(Known.WriteUtc, Known.Length), true));
        }

        [Fact]
        public void Detect_SizeOnlyChanged_IsChange()
        {
            Assert.Equal(FileChange.Reload, FileStampLogic.Detect(Known, new SqlFileStamp(Known.WriteUtc, 101), false));
        }

        [Fact]
        public void Detect_Unknown_IsNone()
        {
            // 기준을 모르거나(이전 임시 저장) 지금 상태를 못 읽으면(접근 오류·응답 없음) 묻지 않는다
            Assert.Equal(FileChange.None, FileStampLogic.Detect(null, new SqlFileStamp(DateTime.UtcNow, 1), true));
            Assert.Equal(FileChange.None, FileStampLogic.Detect(Known, null, true));
        }

        [Fact]
        public void Detect_Deleted_IsMissing_OnlyOnce()
        {
            Assert.Equal(FileChange.Missing, FileStampLogic.Detect(Known, SqlFileStamp.Missing, false));
            Assert.Equal(FileChange.Missing, FileStampLogic.Detect(Known, SqlFileStamp.Missing, true));
            // 알린 뒤 기준을 Missing으로 바꾸면 다시 묻지 않는다
            Assert.Equal(FileChange.None, FileStampLogic.Detect(SqlFileStamp.Missing, SqlFileStamp.Missing, true));
        }

        [Fact]
        public void Detect_Recreated_AfterMissing_IsChange()
        {
            var back = new SqlFileStamp(DateTime.UtcNow, 5);
            Assert.Equal(FileChange.Reload, FileStampLogic.Detect(SqlFileStamp.Missing, back, false));
            Assert.Equal(FileChange.Conflict, FileStampLogic.Detect(SqlFileStamp.Missing, back, true));
        }

        /// <summary>반복 방지: 한 번 알린 변경은 기준을 지금 상태로 옮기므로 다음 포커스에서 다시 묻지 않는다.</summary>
        [Fact]
        public void Detect_AfterAdoptingCurrent_IsNone()
        {
            var changed = new SqlFileStamp(Known.WriteUtc.AddSeconds(5), 120);
            Assert.Equal(FileChange.Conflict, FileStampLogic.Detect(Known, changed, true));

            var adopted = changed;
            Assert.Equal(FileChange.None, FileStampLogic.Detect(adopted, new SqlFileStamp(changed.WriteUtc, changed.Length), true));
            // 그 뒤 또 바뀌면 다시 묻는다
            Assert.Equal(FileChange.Conflict, FileStampLogic.Detect(adopted, new SqlFileStamp(changed.WriteUtc.AddSeconds(1), 120), true));
        }

        [Fact]
        public void Draft_RoundTrip()
        {
            Assert.Equal(Known, FileStampLogic.FromDraft(Known.WriteUtc, Known.Length));
            Assert.Equal(SqlFileStamp.Missing, FileStampLogic.FromDraft(SqlFileStamp.Missing.WriteUtc, SqlFileStamp.Missing.Length));
            Assert.Null(FileStampLogic.FromDraft(null, null));
            Assert.Null(FileStampLogic.FromDraft(Known.WriteUtc, null));
        }
    }
}
