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
        public void Detect_CurrentUnknownOrUnreachable_IsNone()
        {
            // 지금 상태를 못 읽거나(경로 오류) 드라이브에 접근할 수 없으면 묻지 않고 기준도 그대로 둔다
            Assert.Equal(FileChange.None, FileStampLogic.Detect(Known, null, true));
            Assert.Equal(FileChange.None, FileStampLogic.Detect(Known, SqlFileStamp.Unreachable, false));
            Assert.Equal(FileChange.None, FileStampLogic.Detect(null, SqlFileStamp.Unreachable, true));
        }

        /// <summary>기준을 모르는 탭(이전 버전 임시 저장, 창을 열며 파일을 읽지 않고 되살린 탭): 고친 탭은 기준만 잡고, 안 고친 탭은 다시 읽는다.</summary>
        [Fact]
        public void Detect_KnownUnknown_AdoptsOrReloads()
        {
            var current = new SqlFileStamp(DateTime.UtcNow, 1);
            Assert.Equal(FileChange.Adopt, FileStampLogic.Detect(null, current, true));
            Assert.Equal(FileChange.Reload, FileStampLogic.Detect(null, current, false));
            Assert.Equal(FileChange.Missing, FileStampLogic.Detect(null, SqlFileStamp.Missing, true));
            // 접근 불가가 기준으로 남아 있어도 모름과 같다
            Assert.Equal(FileChange.Adopt, FileStampLogic.Detect(SqlFileStamp.Unreachable, current, true));
        }

        /// <summary>재현: 네트워크 드라이브가 끊겨 경로가 바로 "없음"으로 답하면 파일이 지워진 것이 아니라 접근 불가다.</summary>
        [Fact]
        public void Of_RootNotReachable_IsUnreachable_NotMissing()
        {
            var path = Path.Combine(_folder, "none.sql");

            var stamp = FileStampLogic.Of(path, _ => false);

            Assert.True(stamp.IsUnreachable);
            Assert.False(stamp.IsMissing);
            Assert.Equal(FileChange.None, FileStampLogic.Detect(Known, stamp, false));
        }

        [Fact]
        public void Of_RootReachable_FileGone_IsMissing()
        {
            Assert.True(FileStampLogic.Of(Path.Combine(_folder, "none.sql"), _ => true).IsMissing);
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

namespace MyPlugin.Tests
{
    /// <summary>DriveCheckCounter: 드라이브마다 응답 없음을 세어 3번 연속이면 확인을 멈추고, 성공하면 되돌린다.</summary>
    public class DriveCheckCounterTests
    {
        [Theory]
        [InlineData(@"Z:\sql\a.sql", @"Z:\")]
        [InlineData(@"z:\sql\a.sql", @"Z:\")]
        [InlineData(@"\\server\share\sql\a.sql", @"\\server\share")]
        [InlineData(@"\\SERVER\Share", @"\\server\share")]
        [InlineData(@"\\server", @"\\server")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void RootOf(string path, string expected)
        {
            Assert.Equal(expected, DriveCheckCounter.RootOf(path), ignoreCase: true);
        }

        /// <summary>재현: 응답 없는 드라이브를 포커스마다 끝없이 다시 읽지 않는다 — 3번째에 멈추고 그때 한 번만 알린다.</summary>
        [Fact]
        public void StopsAfterThreeTimeouts_ReportsOnce()
        {
            var counter = new DriveCheckCounter();
            const string path = @"Z:\sql\a.sql";

            Assert.False(counter.Fail(path));
            Assert.False(counter.IsStopped(path));
            Assert.False(counter.Fail(path));
            Assert.False(counter.IsStopped(path));
            Assert.True(counter.Fail(path));
            Assert.True(counter.IsStopped(path));
            // 멈춘 뒤 더 세어도 다시 알리지 않는다
            Assert.False(counter.Fail(path));
        }

        [Fact]
        public void SameDrive_SharesCount_OtherDriveIndependent()
        {
            var counter = new DriveCheckCounter();
            counter.Fail(@"\\srv\share\a.sql");
            counter.Fail(@"\\SRV\share\b\c.sql");
            counter.Fail(@"\\srv\SHARE\d.sql");

            Assert.True(counter.IsStopped(@"\\srv\share\other.sql"));
            Assert.False(counter.IsStopped(@"Z:\a.sql"));
            Assert.False(counter.IsStopped(@"\\srv\other\a.sql"));
        }

        [Fact]
        public void Success_Resets()
        {
            var counter = new DriveCheckCounter();
            const string path = @"Z:\a.sql";
            counter.Fail(path);
            counter.Fail(path);
            counter.Succeed(@"Z:\b.sql");
            counter.Fail(path);
            counter.Fail(path);
            Assert.False(counter.IsStopped(path));

            counter.Fail(path);
            Assert.True(counter.IsStopped(path));
            counter.Succeed(path);
            Assert.False(counter.IsStopped(path));
        }

        [Fact]
        public void EmptyPath_NeverStops()
        {
            var counter = new DriveCheckCounter();
            for (var i = 0; i < 5; i++)
                Assert.False(counter.Fail(null));
            Assert.False(counter.IsStopped(null));
        }
    }
}
