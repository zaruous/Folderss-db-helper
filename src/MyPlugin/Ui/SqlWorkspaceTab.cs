using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Windows.Controls;

namespace MyPlugin
{
    /// <summary>결과 영역에서 보이는 칸.</summary>
    internal enum WorkspacePane { Grid, Messages, History }

    /// <summary>
    /// SQL 탭 하나의 상태. 편집기(TextBox)는 탭마다 따로 두어 텍스트·캐럿·선택·스크롤·되돌리기 기록이 탭별로 유지된다.
    /// UI 스레드에서만 읽고 쓴다.
    /// </summary>
    internal sealed class SqlTabState
    {
        private readonly string _baseTitle;

        public SqlTabState(string title, string dbId, TextBox editor)
        {
            _baseTitle = title;
            DbId = dbId;
            Editor = editor;
        }

        /// <summary>탭 이름: 파일 탭이면 파일 이름, 아니면 "SQL n".</summary>
        public string Title
        {
            get { return FilePath != null ? System.IO.Path.GetFileName(FilePath) : _baseTitle; }
        }

        // ---- 파일 ----

        /// <summary>열었거나 저장한 SQL 파일. 없으면 null(저장한 적 없는 탭).</summary>
        public string FilePath { get; set; }

        /// <summary>파일 인코딩(저장할 때 그대로 쓴다). 새 파일은 BOM 없는 UTF-8.</summary>
        public SqlFileEncoding FileEncoding { get; set; } = SqlFileEncoding.Utf8;

        /// <summary>파일의 줄바꿈("\r\n"·"\n"). 편집기는 늘 Environment.NewLine이므로 저장할 때 되돌린다. 모르면 null.</summary>
        public string FileNewline { get; set; }

        /// <summary>파일에서 읽었거나 마지막으로 저장한 때의 편집기 글. null이면 아직 저장하지 않은 내용(파일 탭이면 변경됨).</summary>
        public string SavedText { get; set; }

        /// <summary>파일 탭인데 저장한 뒤 바뀌었음.</summary>
        public bool Dirty
        {
            get { return FilePath != null && !string.Equals(Editor.Text, SavedText, StringComparison.Ordinal); }
        }

        /// <summary>탭 머리에 지금 보이는 변경 표시(바뀔 때만 다시 그린다).</summary>
        public bool ShownDirty { get; set; }

        /// <summary>대상 DB(접속 Id). 없으면 null.</summary>
        public string DbId { get; set; }

        public TextBox Editor { get; }

        /// <summary>탭을 만들 때의 글(첫 탭 안내문·새 탭 머리말). 이것과 같으면 임시 저장하지 않는다. 되살린 탭은 "".</summary>
        public string InitialText { get; set; } = "";

        /// <summary>편집기 첫 줄의 위쪽 위치(스크롤 0 기준). 줄 번호 칸을 맞추려고 처음 배치 뒤에 잰다. 아직 모르면 NaN.</summary>
        public double FirstLineTop { get; set; } = double.NaN;

        // ---- 조회 결과 ----

        /// <summary>마지막 조회의 열. 조회한 적 없으면 null.</summary>
        public List<ResultColumn> Columns { get; private set; }

        public ObservableCollection<ResultGridRow> Rows { get; private set; }

        /// <summary>이 탭의 결과 그리드(처음 조회할 때 만든다).</summary>
        public ResultGridView Grid { get; set; }

        public QueryCursor Cursor { get; private set; }

        public DbSession CursorSession { get; private set; }

        /// <summary>결과(커서)를 만든 DB. 대상을 바꿔도 결과는 이 DB의 것이다.</summary>
        public string ResultDbId { get; private set; }

        /// <summary>이 탭이 커서를 놓았음(대상 변경·탭 닫기·끊기·새 실행). 실제로 닫혔는지와 무관하게 더 가져오지 않는다.</summary>
        public bool CursorReleased { get; set; }

        /// <summary>마지막 실행·가져오기 직후 커서에 행이 더 있었음. 지금 닫혔으면 "끝까지 읽음"이 아니라 중간에 닫힌 것이다.</summary>
        public bool CursorHadMore { get; set; }

        public TimeSpan? LastElapsed { get; set; }

        public bool StrippedSemicolon { get; set; }

        /// <summary>이 결과에서 "가져온 행 안에서만 정렬" 안내를 이미 했음.</summary>
        public bool SortNoticeShown { get; set; }

        /// <summary>열린 커서에서 더 가져올 수 있음.</summary>
        public bool HasMoreRows
        {
            get { return Cursor != null && !CursorReleased && !Cursor.IsClosed && Cursor.HasMore; }
        }

        /// <summary>이 탭이 아직 쥐고 있는 열린 커서가 있음(닫아야 할 서버 자원).</summary>
        public bool HoldsOpenCursor
        {
            get { return Cursor != null && !CursorReleased && !Cursor.IsClosed; }
        }

        public bool CursorClosedEarly
        {
            get { return Cursor != null && CursorHadMore && !HasMoreRows; }
        }

        // ---- 실행 ----

        /// <summary>실행 또는 다음 행 가져오기 중.</summary>
        public bool Running { get; set; }

        public bool Fetching { get; set; }

        public int FetchingCount { get; set; }

        public bool CancelRequested { get; set; }

        public DbSession RunningSession { get; set; }

        public string RunningDbId { get; set; }

        public Stopwatch RunWatch { get; set; }

        /// <summary>
        /// 이 실행의 취소. DbSession은 명령을 등록하기 직전에도 토큰을 보므로, 이전 커서를 닫는 사이 누른 [취소]도 놓치지 않는다.
        /// 타이머·대기 핸들을 쓰지 않으므로 Dispose하지 않는다(취소 작업 스레드와 겹쳐 Dispose하면 안전하지 않음).
        /// </summary>
        public CancellationTokenSource RunCancel { get; set; }

        /// <summary>상태줄 [연결]·[다시 연결]을 눌러 연결하는 중.</summary>
        public bool Connecting { get; set; }

        // ---- 표시 ----

        /// <summary>다른 탭을 보는 동안 실행이 끝났음(탭 머리의 ●).</summary>
        public bool Done { get; set; }

        /// <summary>뒤에서 끝난 실행이 보여 줄 칸. 이 탭으로 옮길 때 적용한다.</summary>
        public WorkspacePane? PendingPane { get; set; }

        public WorkspaceLogic.StatusInfo Status { get; set; }

        /// <summary>새 조회 결과로 바꾼다(첫 묶음).</summary>
        public void SetResult(DbSession session, string dbId, ExecuteResult result)
        {
            Cursor = result.Cursor;
            CursorSession = session;
            ResultDbId = dbId;
            CursorReleased = false;
            SortNoticeShown = false;
            CursorHadMore = result.Cursor != null && result.Cursor.HasMore;
            Columns = result.Cursor != null ? new List<ResultColumn>(result.Cursor.Columns) : new List<ResultColumn>();
            var rows = new List<ResultGridRow>(result.Rows != null ? result.Rows.Count : 0);
            if (result.Rows != null)
            {
                foreach (var values in result.Rows)
                    rows.Add(new ResultGridRow(rows.Count + 1, values));
            }
            Rows = new ObservableCollection<ResultGridRow>(rows);
        }

        /// <summary>가져온 행을 끝에 붙이고 처음 붙인 행을 돌려준다(없으면 null).</summary>
        public ResultGridRow AppendRows(List<string[]> rows)
        {
            ResultGridRow first = null;
            if (Rows == null || rows == null)
                return null;
            foreach (var values in rows)
            {
                var row = new ResultGridRow(Rows.Count + 1, values);
                Rows.Add(row);
                if (first == null)
                    first = row;
            }
            CursorHadMore = Cursor != null && !Cursor.IsClosed && Cursor.HasMore;
            return first;
        }
    }
}
