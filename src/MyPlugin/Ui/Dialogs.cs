using System;
using System.Windows;

namespace MyPlugin
{
    internal enum PendingChoice
    {
        Commit,
        Rollback,
        /// <summary>돌아가기(끊기·닫기를 하지 않음)</summary>
        Cancel
    }

    /// <summary>
    /// 확인 대화상자들. 모두 Theme.ApplyWindow로 테마를 맞춘 모달 창(Owner = 넘겨받은 창, 가운데 정렬, 크기 조절 없음, 작업 표시줄에 안 보임).
    /// 되돌릴 수 없는 선택은 경고 문구 + 확인 체크를 거쳐야 실행 버튼이 켜진다(Folderss 원칙). 기본값은 가장 안전한 선택.
    /// </summary>
    internal static class Dialogs
    {
        /// <summary>
        /// 위험한 문장 실행 확인. 제목 "위험한 문장 실행", 본문: DB 배지 + 접속 표시(user@host:port/service) + "에서 이 문장을 실행할까요?",
        /// 문장 텍스트(고정폭, 스크롤, 최대 높이 제한), 경고(statement.Danger, 위험색), 체크 "대상 DB와 영향을 확인했고 실행합니다", [실행](체크 전 비활성) [취소].
        /// 실행이면 true.
        /// </summary>
        public static bool ConfirmDanger(Window owner, OracleConnectionProfile profile, SqlStatement statement)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 커밋 대기 변경이 있는데 DDL을 실행할 때. "DDL을 실행하면 커밋하지 않은 변경(pendingText)도 함께 커밋되어 되돌릴 수 없습니다." + 확인 체크.
        /// 실행이면 true.
        /// </summary>
        public static bool ConfirmDdlWithPending(Window owner, OracleConnectionProfile profile, string pendingText)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 연결을 끊거나 창을 닫기 전 커밋 대기 변경 처리. action은 "연결을 끊기" / "창을 닫기" 같은 동사구.
        /// 라디오: 롤백(기본, "변경을 버립니다") / 커밋("변경을 DB에 반영합니다"), 버튼 [확인] [돌아가기].
        /// </summary>
        public static PendingChoice AskPending(Window owner, OracleConnectionProfile profile, string pendingText, string action)
        {
            throw new NotImplementedException();
        }

        /// <summary>단순 안내(확인 버튼 하나). error면 위험색 아이콘·문구.</summary>
        public static void Show(Window owner, string title, string message, bool error)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>비밀번호 입력(저장 안 함). 이번 연결에만 쓴다.</summary>
    internal static class PasswordPrompt
    {
        /// <summary>
        /// 제목 "비밀번호 입력 · {이름}", 접속 표시, 이유(reason) 한 줄, PasswordBox, [연결](빈 값이면 비활성) [취소]. Enter로 연결.
        /// 입력한 비밀번호, 취소면 null.
        /// </summary>
        public static string Ask(Window owner, OracleConnectionProfile profile, string reason)
        {
            throw new NotImplementedException();
        }
    }
}
