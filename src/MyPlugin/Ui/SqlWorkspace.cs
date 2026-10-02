using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace MyPlugin
{
    internal enum MessageKind { Info, Success, Error }

    /// <summary>SqlWorkspace가 셸(DbHelperView)에게 받는 기능. 모든 호출은 UI 스레드에서.</summary>
    internal interface IDbHost
    {
        /// <summary>현재 접속 목록(프로필 순서).</summary>
        IReadOnlyList<OracleConnectionProfile> Profiles { get; }

        /// <summary>Id로 접속을 찾는다. 없으면 null.</summary>
        OracleConnectionProfile FindProfile(string dbId);

        /// <summary>연결된 세션. 연결 안 됐으면 null.</summary>
        DbSession GetSession(string dbId);

        /// <summary>연결한다(필요하면 비밀번호를 묻는다). 연결되면 true. 이미 연결돼 있으면 바로 true.
        /// 세션이 끊긴 상태(IsBroken)면 그 세션을 버리고 다시 연결한다(이때 커밋 대기 변경은 이미 서버에서 사라졌다고 안내).</summary>
        Task<bool> ConnectAsync(string dbId);

        /// <summary>트리에서 고른 DB(새 탭의 대상). 없으면 null.</summary>
        string SelectedDbId { get; }

        /// <summary>툴바의 "가져올 행"(200/1000/5000).</summary>
        int FetchCount { get; }

        /// <summary>실행 시작·끝, 커밋 대기 변경 등으로 그 DB의 표시(툴바·트리 배지)를 새로 그려야 할 때.</summary>
        void StateChanged(string dbId);
    }

    /// <summary>
    /// SQL 탭들(탭마다 대상 DB) + 편집기 + 결과(그리드·메시지·기록) + 상태줄. PoC의 오른쪽 영역과 같은 동작.
    /// 화면은 코드로 만든다(XAML 없음). 색은 Theme 도우미로 테마 키에 연결한다.
    /// </summary>
    internal sealed class SqlWorkspace : Grid
    {
        public SqlWorkspace(IDbHost host)
        {
            throw new NotImplementedException();
        }

        /// <summary>지금 탭의 대상 DB Id(없으면 null). 셸의 커밋·롤백·모드 표시가 이 DB를 대상으로 한다.</summary>
        public string ActiveDbId { get { throw new NotImplementedException(); } }

        /// <summary>지금 탭이 바뀌었거나 그 탭의 대상 DB가 바뀌었을 때.</summary>
        public event EventHandler ActiveTabChanged;

        /// <summary>트리에서 테이블·뷰를 두 번 눌렀을 때: 그 DB 대상 탭(지금 탭이 다른 DB면 그 DB의 실행 중 아닌 탭, 없으면 새 탭)으로 옮겨 맨 위에 SELECT를 넣는다.</summary>
        public void InsertSelect(string dbId, string owner, string objectName)
        {
            throw new NotImplementedException();
        }

        /// <summary>메시지 탭에 한 줄(시각, DB 배지, 문장). dbId가 null이면 배지 없이.</summary>
        public void AddMessage(string dbId, string text, MessageKind kind)
        {
            throw new NotImplementedException();
        }

        /// <summary>그 DB를 대상으로 실행·가져오기 중인 탭이 있으면 true.</summary>
        public bool IsRunning(string dbId)
        {
            throw new NotImplementedException();
        }

        public bool AnyRunning { get { throw new NotImplementedException(); } }

        /// <summary>모든 탭의 실행을 취소 요청한다(창 닫기).</summary>
        public void CancelAll()
        {
            throw new NotImplementedException();
        }

        /// <summary>그 DB의 연결을 끊기 직전: 그 DB 탭들의 열린 커서를 닫고 상태줄을 "커서 닫힘"으로.</summary>
        public Task OnDisconnectingAsync(string dbId)
        {
            throw new NotImplementedException();
        }

        /// <summary>접속 목록이 바뀜: 탭 배지·대상 목록 갱신, 삭제된 접속을 대상으로 하던 탭은 대상을 비우고 메시지를 남긴다.</summary>
        public void OnProfilesChanged()
        {
            throw new NotImplementedException();
        }

        public void FocusEditor()
        {
            throw new NotImplementedException();
        }
    }
}
