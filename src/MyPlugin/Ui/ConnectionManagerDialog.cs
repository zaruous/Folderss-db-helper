using System;
using System.Windows;
using Folderss.Plugins;

namespace MyPlugin
{
    /// <summary>접속 관리 대화상자(추가·복제·삭제·편집·접속 테스트). PoC의 "접속 관리" 팝업과 같은 동작.</summary>
    internal static class ConnectionManager
    {
        /// <summary>
        /// 모달로 열고, 저장했으면 true. 연결 중인 접속(ConnectionRepository.IsConnectedAnywhere)은 삭제할 수 없고 "다시 연결할 때 적용" 안내를 보인다.
        /// 저장된 값을 읽지 못하면 안내만 하고 false(덮어쓰지 않음).
        /// </summary>
        public static bool Show(Window owner, IPluginManager manager)
        {
            throw new NotImplementedException();
        }
    }
}
