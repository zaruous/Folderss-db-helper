using System;
using System.Collections.Generic;
using System.Data;

namespace MyPlugin
{
    /// <summary>실행할 SQL과 바인드 값(이름은 ':' 없이, BindByName 기준).</summary>
    public sealed class SqlQuery
    {
        public string Sql { get; set; }
        public List<KeyValuePair<string, object>> Parameters { get; } = new List<KeyValuePair<string, object>>();
    }

    public sealed class SchemaInfo
    {
        public string Name { get; set; }
        /// <summary>Oracle 내장 스키마(ALL_USERS.ORACLE_MAINTAINED = 'Y', 11g는 <see cref="OracleMetadata.KnownSystemSchemas"/>).</summary>
        public bool OracleMaintained { get; set; }
    }

    public sealed class DbObjectInfo
    {
        public string Owner { get; set; }
        public string Name { get; set; }
        /// <summary>ALL_OBJECTS.OBJECT_TYPE (TABLE, VIEW, SEQUENCE, PROCEDURE, FUNCTION, PACKAGE)</summary>
        public string Type { get; set; }
        /// <summary>테이블 통계 행 수(ALL_TABLES.NUM_ROWS). 통계가 없거나 테이블이 아니면 null.</summary>
        public long? NumRows { get; set; }
        /// <summary>ALL_OBJECTS.STATUS (VALID/INVALID)</summary>
        public string Status { get; set; }
    }

    public sealed class ColumnInfo
    {
        public string Owner { get; set; }
        /// <summary>테이블 또는 뷰 이름</summary>
        public string ObjectName { get; set; }
        public string Name { get; set; }
        /// <summary>표시용 형식. 예: VARCHAR2(10 CHAR), NUMBER(7,2), NUMBER, DATE, TIMESTAMP(6)</summary>
        public string TypeLabel { get; set; }
        public bool Nullable { get; set; }
        public bool PrimaryKey { get; set; }
        public int Position { get; set; }
    }

    /// <summary>트리 검색 결과 한 건. 객체 검색이면 ColumnName이 null.</summary>
    public sealed class SearchHit
    {
        public string Owner { get; set; }
        public string ObjectType { get; set; }
        public string ObjectName { get; set; }
        public string ColumnName { get; set; }
        public string ColumnType { get; set; }
    }

    /// <summary>
    /// 트리·검색용 데이터 사전 조회 SQL과 결과 읽기(순수 로직, 테스트 대상).
    /// - ALL_* 뷰만 쓴다(DBA_*는 권한 필요). 바인드는 이름 바인드(:owner 등).
    /// - 11g 호환: 행 제한은 ROWNUM, ALL_USERS.ORACLE_MAINTAINED가 없으면(ORA-00904) KnownSystemSchemas로 대신한다.
    /// - 행 제한 쿼리는 limit+1행까지 돌려준다(호출자가 "더 있음"을 판단). limit 0은 제한 없음.
    /// - 휴지통 객체(BIN$…)는 뺀다.
    /// - 검색은 UPPER(이름) LIKE :pattern ESCAPE '\' (pattern은 SqlScript.ContainsPattern). includeSystem=false면 내장 스키마의 결과를 뺀다.
    /// </summary>
    public static class OracleMetadata
    {
        public const int SearchLimit = 500;

        /// <summary>ORACLE_MAINTAINED가 없는 DB(11g)에서 내장으로 볼 스키마.</summary>
        public static readonly string[] KnownSystemSchemas =
        {
            "ANONYMOUS", "APEX_PUBLIC_USER", "APPQOSSYS", "AUDSYS", "CTXSYS", "DBSFWUSER", "DBSNMP", "DIP", "DVF", "DVSYS",
            "FLOWS_FILES", "GGSYS", "GSMADMIN_INTERNAL", "GSMCATUSER", "GSMUSER", "LBACSYS", "MDDATA", "MDSYS", "OJVMSYS",
            "OLAPSYS", "ORACLE_OCM", "ORDDATA", "ORDPLUGINS", "ORDSYS", "OUTLN", "REMOTE_SCHEDULER_AGENT", "SI_INFORMTN_SCHEMA",
            "SPATIAL_CSW_ADMIN_USR", "SPATIAL_WFS_ADMIN_USR", "SYS", "SYS$UMF", "SYSBACKUP", "SYSDG", "SYSKM", "SYSRAC", "SYSTEM",
            "WMSYS", "XDB", "XS$NULL"
        };

        /// <summary>SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM DUAL</summary>
        public static SqlQuery CurrentSchema() { throw new NotImplementedException(); }

        /// <summary>ALL_USERS의 USERNAME(+ORACLE_MAINTAINED), USERNAME 순.</summary>
        public static SqlQuery Schemas(bool hasOracleMaintained) { throw new NotImplementedException(); }

        public static SchemaInfo ReadSchema(IDataRecord record, bool hasOracleMaintained) { throw new NotImplementedException(); }

        /// <summary>스키마 하나의 객체 종류별 개수: SELECT OBJECT_TYPE, COUNT(*) … WHERE OWNER = :owner AND OBJECT_TYPE IN (…) GROUP BY OBJECT_TYPE.</summary>
        public static SqlQuery GroupCounts(string owner) { throw new NotImplementedException(); }

        /// <summary>GroupCounts 결과를 묶음(TreeGroups) → 개수로. PROCEDURE·FUNCTION·PACKAGE는 CODE로 합친다. 없는 묶음은 0.</summary>
        public static Dictionary<string, int> ReadGroupCounts(IDataReader reader) { throw new NotImplementedException(); }

        /// <summary>스키마 하나의 묶음(TreeGroups) 객체 목록, 이름 순. TABLE은 ALL_TABLES.NUM_ROWS를 함께 읽는다(LEFT JOIN). limit+1행까지.</summary>
        public static SqlQuery Objects(string owner, string group, int limit) { throw new NotImplementedException(); }

        public static DbObjectInfo ReadObject(IDataRecord record) { throw new NotImplementedException(); }

        /// <summary>테이블·뷰의 열(ALL_TAB_COLUMNS) COLUMN_ID 순 + 기본 키 여부(ALL_CONSTRAINTS 'P' / ALL_CONS_COLUMNS).</summary>
        public static SqlQuery Columns(string owner, string objectName) { throw new NotImplementedException(); }

        public static ColumnInfo ReadColumn(IDataRecord record) { throw new NotImplementedException(); }

        /// <summary>
        /// ALL_TAB_COLUMNS 값으로 표시 형식을 만든다. 예:
        /// VARCHAR2·NVARCHAR2·CHAR·NCHAR → "VARCHAR2(10 CHAR)"/"VARCHAR2(10 BYTE)"(CHAR_USED 'C'면 CHAR_LENGTH와 CHAR, 아니면 DATA_LENGTH와 BYTE; N 형식은 단위 없이 CHAR_LENGTH),
        /// NUMBER: 정밀도 없으면 "NUMBER", 소수 자릿수 0이면 "NUMBER(p)", 아니면 "NUMBER(p,s)"; FLOAT(p); RAW(n);
        /// 그 밖(DATE, CLOB, TIMESTAMP(6), TIMESTAMP(6) WITH TIME ZONE …)은 DATA_TYPE 그대로.
        /// </summary>
        public static string FormatColumnType(string dataType, decimal? dataLength, decimal? precision, decimal? scale, string charUsed, decimal? charLength)
        {
            throw new NotImplementedException();
        }

        /// <summary>스키마 이름 검색(ALL_USERS). limit+1행까지.</summary>
        public static SqlQuery SearchSchemas(string pattern, bool includeSystem, bool hasOracleMaintained, int limit) { throw new NotImplementedException(); }

        /// <summary>객체 이름 검색(ALL_OBJECTS, 트리에 보이는 종류만). OWNER, OBJECT_TYPE, OBJECT_NAME 순. limit+1행까지.</summary>
        public static SqlQuery SearchObjects(string pattern, bool includeSystem, bool hasOracleMaintained, int limit) { throw new NotImplementedException(); }

        /// <summary>열 이름 검색(ALL_TAB_COLUMNS, 테이블·뷰). OWNER, TABLE_NAME, COLUMN_ID 순. 결과의 ObjectType은 TABLE 또는 VIEW. limit+1행까지.</summary>
        public static SqlQuery SearchColumns(string pattern, bool includeSystem, bool hasOracleMaintained, int limit) { throw new NotImplementedException(); }

        public static SearchHit ReadObjectHit(IDataRecord record) { throw new NotImplementedException(); }

        public static SearchHit ReadColumnHit(IDataRecord record) { throw new NotImplementedException(); }

        /// <summary>ORA-00904(잘못된 식별자) — ORACLE_MAINTAINED 열이 없는 11g에서 대체 쿼리로 다시 시도할 때 판단한다. 예외 메시지·OracleException.Number로 판단.</summary>
        public static bool IsInvalidIdentifier(Exception ex) { throw new NotImplementedException(); }
    }
}
