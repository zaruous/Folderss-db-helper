using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyPlugin
{
    /// <summary>
    /// 테이블 정보(F4)와 구조 편집의 DBMS별 구현. <see cref="IDbProvider.TableSchema"/>로 얻는다(지원하지 않는 공급자는 null).
    /// 화면(TableInfoWindow·TableSchemaEditor)과 편집 모델(SchemaRow·SchemaPlan)은 이 인터페이스와 중립 모델(TableDescription)만 안다.
    /// </summary>
    internal interface ITableSchemaProvider
    {
        /// <summary>
        /// 대상 찾기(스키마 생략 시 현재 스키마 → 동의어 → 다른 스키마) → 요약 → 컬럼 → (테이블이면) 인덱스·제약 조건.
        /// 찾지 못했거나 여러 스키마에 있어 정할 수 없으면 error에 사람이 읽을 문장을 주고 null.
        /// </summary>
        Task<TableDescription> DescribeAsync(DbSession session, string owner, string name, CancellationToken token, Action<string> error);

        /// <summary>
        /// 테이블 정의서용: 스키마 하나의 테이블·뷰 여럿을 이름 목록(IN)으로 한 번에 읽는다(요약·컬럼·인덱스·제약 각 1회).
        /// 이름은 정확히 같아야 하고(대상 찾기·동의어 없음), 찾지 못한 이름은 빠진다. 결과는 names 순서. 이름 수는 <see cref="TableSpecQueries.ChunkSize"/> 이하.
        /// </summary>
        Task<List<TableDescription>> DescribeManyAsync(DbSession session, string owner, IReadOnlyList<string> names, CancellationToken token);

        /// <summary>구조 편집 DDL 방언. 편집을 지원하지 않으면 null(정보 창은 읽기 전용).</summary>
        ISchemaDdl Ddl { get; }
    }

    /// <summary>편집 대상 테이블.</summary>
    internal sealed class SchemaTarget
    {
        public SchemaTarget(string owner, string table) { Owner = owner; Table = table; }
        public string Owner { get; }
        public string Table { get; }
    }

    /// <summary>컬럼 정의(DDL 조립용). Default·DataType은 SQL 조각 그대로.</summary>
    internal sealed class ColumnSpec
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool NotNull { get; set; }
        public string Default { get; set; }
    }

    /// <summary>제약 조건 정의: 종류 P·U·R·C, 키 컬럼, 외래 키 참조("OWNER.TABLE (COL, …)" SQL 조각), 검사 조건(SQL 조각).</summary>
    internal sealed class ConstraintSpec
    {
        public string Type { get; set; }
        public IReadOnlyList<IndexColumnChoice> Columns { get; set; } = Array.Empty<IndexColumnChoice>();
        public string Reference { get; set; }
        public string Condition { get; set; }
    }

    /// <summary>자료형 고르기 목록 한 줄(코드 보기 팝업).</summary>
    internal sealed class DataTypeChoice
    {
        public DataTypeChoice(string type, string title) { Type = type; Title = title; }
        public string Type { get; }
        public string Title { get; }
    }

    /// <summary>
    /// 구조 편집 DDL 방언. 메서드마다 실행 단위 하나(SQL 텍스트, 끝 ';' 없음)를 돌려준다 — SQL Server처럼 한 작업에 여러 문장이 필요하면
    /// 한 배치로 묶어 돌려준다(다시 실행해도 안전하게). 실행 순서·부분 실패 재시도는 SchemaPlan이 맡는다.
    /// 이름·자료형·식은 호출자가 검증한 값이지만, 문장 구분자·주석이 든 SQL 조각은 여기서도 거부한다(ArgumentException).
    /// </summary>
    internal interface ISchemaDdl
    {
        /// <summary>이름 최대 길이(Oracle 128, PostgreSQL 63, SQL Server 128).</summary>
        int MaxNameLength { get; }
        /// <summary>자료형 고르기 목록(직접 입력도 된다).</summary>
        IReadOnlyList<DataTypeChoice> DataTypes { get; }
        /// <summary>이 종류(P·U·R·C)의 제약을 끄고 켤 수 있는지(PostgreSQL은 모두 불가, SQL Server는 R·C만).</summary>
        bool CanDisable(string constraintType);

        string AddColumn(SchemaTarget target, ColumnSpec column);
        string DropColumn(SchemaTarget target, string column);
        string RenameColumn(SchemaTarget target, string from, string to);
        /// <summary>자료형 바꾸기. column은 바뀐 뒤의 전체 정의(SQL Server는 NULL 여부도 함께 적어야 한다).</summary>
        string ChangeType(SchemaTarget target, ColumnSpec column);
        string ChangeNullability(SchemaTarget target, ColumnSpec column);
        /// <summary>기본값 바꾸기. column.Default가 비면 기본값을 없앤다.</summary>
        string ChangeDefault(SchemaTarget target, ColumnSpec column);
        /// <summary>컬럼 주석. 비면 주석을 없앤다.</summary>
        string CommentColumn(SchemaTarget target, string column, string comment);

        string CreateIndex(SchemaTarget target, string name, bool unique, IReadOnlyList<IndexColumnChoice> columns);
        string DropIndex(SchemaTarget target, string name);
        string RenameIndex(SchemaTarget target, string from, string to);

        string AddConstraint(SchemaTarget target, string name, ConstraintSpec spec);
        string DropConstraint(SchemaTarget target, string name);
        string RenameConstraint(SchemaTarget target, string from, string to);
        string SetConstraintEnabled(SchemaTarget target, string name, string constraintType, bool enabled);
    }

    /// <summary>
    /// 방언 공통 부분: 식별자 따옴표·SQL 조각 검사·제약 정의 조립. 표준 SQL에 가까운 문장(ALTER TABLE … ADD CONSTRAINT 등)은 기본 구현을 두고
    /// DBMS마다 다른 것만 덮어쓴다.
    /// </summary>
    internal abstract class SchemaDdlBase : ISchemaDdl
    {
        public abstract int MaxNameLength { get; }
        public abstract IReadOnlyList<DataTypeChoice> DataTypes { get; }
        public virtual bool CanDisable(string constraintType) => true;

        /// <summary>식별자 하나를 따옴표로 감싼다(앞뒤 공백 제거, 제어 문자·빈 이름 거부).</summary>
        public abstract string Quote(string name);

        protected static string Checked(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) throw new ArgumentException("이름을 입력하세요.");
            return name.Trim();
        }

        public virtual string Table(SchemaTarget target) => Quote(target.Owner) + "." + Quote(target.Table);

        /// <summary>SQL 조각(자료형·기본값·조건·참조): 비지 않고 문장 구분자·주석이 없어야 한다.</summary>
        protected static string Fragment(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0 || value.Contains(";") || value.Contains("--") || value.Contains("/*") || value.Contains("*/"))
                throw new ArgumentException("SQL 정의를 입력하세요. 문장 구분자와 주석은 사용할 수 없습니다.");
            return value;
        }

        protected static string Literal(string text) => "'" + (text ?? "").Replace("'", "''") + "'";

        public string KeyList(IEnumerable<IndexColumnChoice> columns, bool allowDescending)
        {
            var list = columns.Select(c => Quote(c.Name) + (allowDescending && c.Descending ? " DESC" : "")).ToArray();
            if (list.Length == 0) throw new ArgumentException("컬럼을 하나 이상 고르세요.");
            return string.Join(", ", list);
        }

        protected string ConstraintDefinition(ConstraintSpec spec)
        {
            switch (spec.Type)
            {
                case "P": return "PRIMARY KEY (" + KeyList(spec.Columns, false) + ")";
                case "U": return "UNIQUE (" + KeyList(spec.Columns, false) + ")";
                case "R": return "FOREIGN KEY (" + KeyList(spec.Columns, false) + ") REFERENCES " + Fragment(spec.Reference);
                case "C": return "CHECK (" + Fragment(spec.Condition) + ")";
                default: throw new ArgumentException("제약 종류는 P·U·R·C 중 하나입니다.");
            }
        }

        public abstract string AddColumn(SchemaTarget target, ColumnSpec column);
        public virtual string DropColumn(SchemaTarget target, string column) => "ALTER TABLE " + Table(target) + " DROP COLUMN " + Quote(column);
        public virtual string RenameColumn(SchemaTarget target, string from, string to) => "ALTER TABLE " + Table(target) + " RENAME COLUMN " + Quote(from) + " TO " + Quote(to);
        public abstract string ChangeType(SchemaTarget target, ColumnSpec column);
        public abstract string ChangeNullability(SchemaTarget target, ColumnSpec column);
        public abstract string ChangeDefault(SchemaTarget target, ColumnSpec column);
        public virtual string CommentColumn(SchemaTarget target, string column, string comment)
            => "COMMENT ON COLUMN " + Table(target) + "." + Quote(column) + " IS " + Literal(comment);

        public virtual string CreateIndex(SchemaTarget target, string name, bool unique, IReadOnlyList<IndexColumnChoice> columns)
            => "CREATE " + (unique ? "UNIQUE " : "") + "INDEX " + Quote(target.Owner) + "." + Quote(name) + " ON " + Table(target) + " (" + KeyList(columns, true) + ")";
        public virtual string DropIndex(SchemaTarget target, string name) => "DROP INDEX " + Quote(target.Owner) + "." + Quote(name);
        public virtual string RenameIndex(SchemaTarget target, string from, string to) => "ALTER INDEX " + Quote(target.Owner) + "." + Quote(from) + " RENAME TO " + Quote(to);

        public virtual string AddConstraint(SchemaTarget target, string name, ConstraintSpec spec)
            => "ALTER TABLE " + Table(target) + " ADD CONSTRAINT " + Quote(name) + " " + ConstraintDefinition(spec);
        public virtual string DropConstraint(SchemaTarget target, string name) => "ALTER TABLE " + Table(target) + " DROP CONSTRAINT " + Quote(name);
        public virtual string RenameConstraint(SchemaTarget target, string from, string to) => "ALTER TABLE " + Table(target) + " RENAME CONSTRAINT " + Quote(from) + " TO " + Quote(to);
        public abstract string SetConstraintEnabled(SchemaTarget target, string name, string constraintType, bool enabled);
    }

    /// <summary>"..."로 감싸는 방언(Oracle·PostgreSQL).</summary>
    internal abstract class DoubleQuoteDdl : SchemaDdlBase
    {
        public override string Quote(string name) => "\"" + Checked(name).Replace("\"", "\"\"") + "\"";
    }

    /// <summary>DBMS별 메타데이터를 읽어 TableDescription을 만드는 데 쓰는 공통 도우미.</summary>
    internal static class SchemaRead
    {
        public static string Text(IDataRecord record, string column) => TableInfo.Text(record, column);
        public static decimal? Number(IDataRecord record, string column) => TableInfo.Number(record, column);

        public static bool Flag(IDataRecord record, string column)
        {
            var value = record[column];
            if (value == null || value is DBNull) return false;
            if (value is bool flag) return flag;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        }

        public static void Bind(SqlQuery query, string name, object value)
            => query.Parameters.Add(new KeyValuePair<string, object>(name, value ?? DBNull.Value));

        /// <summary>이름 목록을 t0, t1, …로 바인드하고 "(:t0, :t1)"(prefix가 ':') 꼴 IN 목록을 돌려준다. 1~ChunkSize개.</summary>
        public static string BindNames(SqlQuery query, string prefix, IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0 || names.Count > TableSpecQueries.ChunkSize)
                throw new ArgumentException("이름은 1~" + TableSpecQueries.ChunkSize + "개여야 합니다.", nameof(names));
            var binds = new List<string>(names.Count);
            for (var i = 0; i < names.Count; i++)
            {
                var key = "t" + i.ToString(CultureInfo.InvariantCulture);
                Bind(query, key, names[i]);
                binds.Add(prefix + key);
            }
            return "(" + string.Join(", ", binds) + ")";
        }

        /// <summary>
        /// 정의서용 일괄 조회 공통 순서: 요약(OBJECT_NAME·OBJECT_TYPE) → 컬럼(TABLE_NAME) → 테이블만 인덱스·제약(테이블 이름별).
        /// </summary>
        public static async Task<List<TableDescription>> DescribeManyAsync(DbSession session, string owner, IReadOnlyList<string> names, CancellationToken token,
            Func<string, IReadOnlyList<string>, SqlQuery> summary,
            Func<string, IReadOnlyList<string>, SqlQuery> columns, Func<IDataRecord, DescribedColumn> readColumn,
            Func<DbSession, string, IReadOnlyList<string>, CancellationToken, Task<ILookup<string, IndexDescription>>> indexes,
            Func<DbSession, string, IReadOnlyList<string>, CancellationToken, Task<ILookup<string, ConstraintDescription>>> constraints)
        {
            var tables = new Dictionary<string, TableDescription>(StringComparer.Ordinal);
            await session.QueryAsync(summary(owner, names), r =>
            {
                var info = new TableDescription { Owner = owner, Name = Text(r, "OBJECT_NAME"), Type = Text(r, "OBJECT_TYPE") };
                TableInfo.ReadSummary(r, info);
                if (info.Name != null && !tables.ContainsKey(info.Name))
                    tables.Add(info.Name, info);
                return true;
            }, token).ConfigureAwait(true);
            var found = names.Where(tables.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
            if (found.Count == 0)
                return new List<TableDescription>();
            foreach (var column in await session.QueryAsync(columns(owner, found), readColumn, token).ConfigureAwait(true))
            {
                TableDescription info;
                if (tables.TryGetValue(column.Column.ObjectName ?? "", out info))
                    info.Columns.Add(column);
            }
            var tableNames = found.Where(n => string.Equals(tables[n].Type, "TABLE", StringComparison.Ordinal)).ToList();
            if (tableNames.Count > 0)
            {
                var byIndex = await indexes(session, owner, tableNames, token).ConfigureAwait(true);
                var byConstraint = await constraints(session, owner, tableNames, token).ConfigureAwait(true);
                foreach (var name in tableNames)
                {
                    tables[name].Indexes.AddRange(byIndex[name]);
                    tables[name].Constraints.AddRange(byConstraint[name]);
                }
            }
            return found.Select(n => tables[n]).ToList();
        }

        /// <summary>식 전체를 감싼 괄호를 벗긴다: "((0))" → "0", "(getdate())" → "getdate()", "(a) + (b)"는 그대로.</summary>
        public static string Unwrap(string expression)
        {
            var text = (expression ?? "").Trim();
            while (text.Length >= 2 && text[0] == '(' && text[text.Length - 1] == ')' && Closes(text))
                text = text.Substring(1, text.Length - 2).Trim();
            return text;
        }

        /// <summary>맨 앞 '('의 짝이 맨 끝 ')'인지(따옴표 안은 건너뜀).</summary>
        static bool Closes(string text)
        {
            int depth = 0; bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '\'') quoted = !quoted;
                if (quoted) continue;
                if (ch == '(') depth++;
                else if (ch == ')' && --depth == 0) return i == text.Length - 1;
            }
            return false;
        }

        /// <summary>구조 편집이 만든 SQL(끝 ';' 없음)을 DDL 문장으로 — 사용자 SQL처럼 문장 종류를 추측하지 않는다.</summary>
        public static SqlStatement DdlStatement(string sql)
        {
            var text = (sql ?? "").Trim();
            if (text.Length == 0) throw new ArgumentException("실행할 문장이 없습니다.");
            var verb = new string(text.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
            return new SqlStatement { Text = text, Kind = SqlKind.Ddl, Verb = verb, Length = text.Length };
        }

        /// <summary>정보 조회 공통 순서: 대상 찾기 → 요약 → 컬럼 → (테이블이면) 인덱스·제약.</summary>
        public static async Task<TableDescription> DescribeAsync(DbSession session, string owner, string name, CancellationToken token, Action<string> error,
            Func<string, string, SqlQuery> resolve,
            Func<string, string, SqlQuery> summary,
            Func<string, string, SqlQuery> columns, Func<IDataRecord, DescribedColumn> readColumn,
            Func<DbSession, string, string, CancellationToken, Task<List<IndexDescription>>> indexes,
            Func<DbSession, string, string, CancellationToken, Task<List<ConstraintDescription>>> constraints)
        {
            var candidates = await session.QueryAsync(resolve(owner, name), TableInfo.ReadResolved, token).ConfigureAwait(true);
            var resolution = TableInfo.Resolve(candidates);
            var display = owner == null ? name : owner + "." + name;
            if (resolution.AmbiguousOwners.Count > 0) { error(TableInfo.NotUniqueMessage(display, resolution.AmbiguousOwners, name)); return null; }
            if (resolution.Object == null) { error(TableInfo.NotFoundMessage(display)); return null; }
            var target = resolution.Object;
            var info = new TableDescription { Owner = target.Owner, Name = target.Name, Type = target.Type, Via = target.Via };
            await session.QueryAsync(summary(target.Owner, target.Name), r => { TableInfo.ReadSummary(r, info); return true; }, token).ConfigureAwait(true);
            info.Columns.AddRange(await session.QueryAsync(columns(target.Owner, target.Name), readColumn, token).ConfigureAwait(true));
            if (string.Equals(target.Type, "TABLE", StringComparison.Ordinal))
            {
                info.Indexes.AddRange(await indexes(session, target.Owner, target.Name, token).ConfigureAwait(true));
                info.Constraints.AddRange(await constraints(session, target.Owner, target.Name, token).ConfigureAwait(true));
            }
            return info;
        }
    }
}
