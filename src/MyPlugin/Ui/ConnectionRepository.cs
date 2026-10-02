using Folderss.Plugins;
using System;
using System.Collections.Generic;

namespace MyPlugin
{
    /// <summary>
    /// 접속 목록 읽기·저장(플러그인 설정 키 <see cref="OracleConnectionStore.SettingKey"/>)과, 열려 있는 DB Helper 창들 사이의 알림.
    /// 편집은 접속 관리 대화상자에서만 한다(설정 창 탭은 목록과 [접속 관리 열기]만).
    /// </summary>
    internal static class ConnectionRepository
    {
        /// <summary>저장된 접속 목록. 저장값이 손상됐으면 InvalidOperationException("저장된 접속 정보를 읽지 못했습니다: …").</summary>
        public static List<OracleConnectionProfile> Load(IPluginManager manager)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 검증(OracleConnectionStore.Validate) 후 저장한다. 검증 실패면 InvalidOperationException(오류 문장들을 줄바꿈으로).
        /// 저장 실패(SetSetting 예외)는 그대로 던진다. 성공하면 <see cref="Changed"/>를 UI 스레드에서 알린다.
        /// </summary>
        public static void Save(IPluginManager manager, IList<OracleConnectionProfile> profiles)
        {
            throw new NotImplementedException();
        }

        /// <summary>접속 목록이 저장됐을 때(열린 DB Helper 창들이 트리·탭을 새로 그림).</summary>
        public static event EventHandler Changed;

        /// <summary>
        /// 저장된 비밀번호를 푼다. 저장된 비밀번호가 없으면 null.
        /// 풀 수 없으면(다른 PC·다른 Windows 사용자가 저장) InvalidOperationException("저장된 비밀번호를 풀 수 없습니다 …").
        /// </summary>
        public static string StoredPassword(OracleConnectionProfile profile)
        {
            throw new NotImplementedException();
        }

        /// <summary>이 프로세스의 DB Helper 창에서 지금 연결 중인 접속 Id 수 세기(접속 관리에서 연결 중인 접속 삭제를 막는 데 씀).</summary>
        public static void MarkConnected(string profileId)
        {
            throw new NotImplementedException();
        }

        public static void MarkDisconnected(string profileId)
        {
            throw new NotImplementedException();
        }

        public static bool IsConnectedAnywhere(string profileId)
        {
            throw new NotImplementedException();
        }
    }
}
