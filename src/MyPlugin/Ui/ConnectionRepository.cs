using Folderss.Plugins;
using System;
using System.Collections.Generic;
using System.Windows;

namespace MyPlugin
{
    /// <summary>
    /// 접속 목록 읽기·저장(플러그인 설정 키 <see cref="OracleConnectionStore.SettingKey"/>)과, 열려 있는 DB Helper 창들 사이의 알림.
    /// 편집은 접속 관리 대화상자에서만 한다(설정 창 탭은 목록과 [접속 관리 열기]만).
    /// </summary>
    internal static class ConnectionRepository
    {
        private static readonly ConnectionUseCounts Connected = new ConnectionUseCounts();

        /// <summary>저장된 접속 목록. 저장값이 손상됐으면 InvalidOperationException("저장된 접속 정보를 읽지 못했습니다: …").</summary>
        public static List<OracleConnectionProfile> Load(IPluginManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            string json;
            try
            {
                json = manager.GetSetting(OracleConnectionStore.SettingKey);
            }
            catch (Exception ex)
            {
                throw ConnectionManagerLogic.LoadFailed(ex);
            }
            return ConnectionManagerLogic.ParseStored(json);
        }

        /// <summary>
        /// 검증(OracleConnectionStore.Validate) 후 저장한다. 검증 실패면 InvalidOperationException(오류 문장들을 줄바꿈으로).
        /// 저장 실패(SetSetting 예외)는 그대로 던진다. 성공하면 <see cref="Changed"/>를 UI 스레드에서 알린다.
        /// </summary>
        public static void Save(IPluginManager manager, IList<OracleConnectionProfile> profiles)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (profiles == null)
                throw new ArgumentNullException(nameof(profiles));
            manager.SetSetting(OracleConnectionStore.SettingKey, ConnectionManagerLogic.SerializeForSave(profiles));
            RaiseChanged();
        }

        /// <summary>접속 목록이 저장됐을 때(열린 DB Helper 창들이 트리·탭을 새로 그림).</summary>
        public static event EventHandler Changed;

        /// <summary>
        /// 저장된 비밀번호를 푼다. 저장된 비밀번호가 없으면 null.
        /// 풀 수 없으면(다른 PC·다른 Windows 사용자가 저장) InvalidOperationException("저장된 비밀번호를 풀 수 없습니다 …").
        /// </summary>
        public static string StoredPassword(OracleConnectionProfile profile)
        {
            return ConnectionManagerLogic.StoredPassword(profile);
        }

        /// <summary>이 프로세스의 DB Helper 창에서 지금 연결 중인 접속 Id 수 세기(접속 관리에서 연결 중인 접속 삭제를 막는 데 씀).</summary>
        public static void MarkConnected(string profileId)
        {
            Connected.Add(profileId);
        }

        public static void MarkDisconnected(string profileId)
        {
            Connected.Remove(profileId);
        }

        public static bool IsConnectedAnywhere(string profileId)
        {
            return Connected.Contains(profileId);
        }

        private static void RaiseChanged()
        {
            var app = Application.Current;
            var dispatcher = app == null ? null : app.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.Invoke(new Action(NotifySubscribers));
            else
                NotifySubscribers();
        }

        // 처리기 하나가 실패해도 나머지 창은 새로 그리게 하고, 실패는 저장 실패와 구별해 알린다(이미 저장됐음).
        private static void NotifySubscribers()
        {
            var handler = Changed;
            if (handler == null)
                return;
            Exception first = null;
            foreach (EventHandler subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(null, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    if (first == null)
                        first = ex;
                }
            }
            if (first != null)
                throw new NotifyFailedException(first);
        }

        /// <summary>저장은 됐지만 <see cref="Changed"/> 처리기 일부가 실패함. 저장 실패와 구별하려고 따로 둔다.</summary>
        internal sealed class NotifyFailedException : Exception
        {
            public NotifyFailedException(Exception inner)
                : base("접속 정보는 저장했지만 열린 창 일부를 새로 그리지 못했습니다: " + inner.Message, inner)
            {
            }
        }
    }
}
