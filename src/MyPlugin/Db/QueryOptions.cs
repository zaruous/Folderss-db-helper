namespace MyPlugin
{
    /// <summary>
    /// 조회 옵션 기본값 및 현재 설정값.
    /// Db/* 및 순수 로직에서도 참조하므로 WPF 종속성이 없다.
    /// </summary>
    internal static class QueryOptions
    {
        public const int DefaultRows = 100;
        public const int MaxRows = 10000;
        public const int DefaultFetchBytes = 131072;
        public const int MinFetchBytes = 8192;
        public const int MaxFetchBytes = 67108864;

        /// <summary>트리 F1 빠른 조회·WHERE 다시 조회로 가져올 행 수(기본 100).</summary>
        internal static int QuickRows = DefaultRows;

        /// <summary>Oracle OracleCommand.FetchSize(바이트, 기본 131,072 = ODP.NET 기본값).</summary>
        internal static int FetchBytes = DefaultFetchBytes;
    }
}
