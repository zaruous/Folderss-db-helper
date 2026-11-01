using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace MyPlugin
{
    /// <summary>실행할 DDL 한 문장. Phase 순으로 실행하고, 성공하면 Applied로 행의 원래 값을 갱신한다(부분 실패 후 재시도는 남은 것만).</summary>
    internal sealed class SchemaStep
    {
        public string Sql { get; set; }
        public Action Applied { get; set; }
        /// <summary>실행 순서: 0 제약 삭제 → 1 인덱스 삭제 → 2 컬럼 → 3 인덱스 생성·이름 → 4 제약 추가·이름 → 5 제약 활성/비활성.</summary>
        public int Phase { get; set; }
        /// <summary>데이터를 잃는 삭제(DROP COLUMN)면 true — 확인 창에서 따로 확인받는다.</summary>
        public bool Destructive { get; set; }
    }

    /// <summary>저장 시점 검증 오류 하나: 어느 행의 어느 칸인지와 이유.</summary>
    internal sealed class SchemaError
    {
        public SchemaRow Row { get; set; }
        public string Field { get; set; }
        public string Message { get; set; }
    }

    /// <summary>검증·DDL 계획에 필요한 테이블 전체 상황.</summary>
    internal sealed class SchemaContext
    {
        public string Owner { get; set; }
        public string Table { get; set; }
        /// <summary>DBMS별 DDL 방언(Oracle·PostgreSQL·SQL Server).</summary>
        public ISchemaDdl Ddl { get; set; } = new OracleSchemaDdl();
        public SchemaTarget Target => new SchemaTarget(Owner, Table);
        public IList<ColumnRow> Columns { get; set; } = new List<ColumnRow>();
        public IList<IndexRow> Indexes { get; set; } = new List<IndexRow>();
        public IList<ConstraintRow> Constraints { get; set; } = new List<ConstraintRow>();

        /// <summary>저장 후 남을 컬럼 이름(삭제 표시 제외, 이름 변경 반영).</summary>
        public string[] LiveColumns => Columns.Where(c => !c.Deleted && !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name.Trim()).ToArray();
        public IEnumerable<SchemaRow> All => Columns.Cast<SchemaRow>().Concat(Indexes).Concat(Constraints);
    }

    /// <summary>
    /// 구조 편집 표의 한 행(컬럼·인덱스·제약 조건 공통). 값은 이름 붙은 칸으로 갖고, 원래 값과 비교해 상태(추가·수정·삭제)를 낸다.
    /// 검증은 편집 중에 하지 않고 [저장] 때 SchemaPlan.Validate가 한꺼번에 한다 — 오류는 INotifyDataErrorInfo로 해당 칸에 표시되고, 그 칸을 고치면 사라진다.
    /// </summary>
    internal abstract class SchemaRow : INotifyPropertyChanged, INotifyDataErrorInfo
    {
        static readonly string[] Derived = { nameof(State), nameof(Dirty), nameof(Mark), nameof(HasErrors), nameof(ErrorText) };
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> errors = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> original;
        bool deleted;

        protected SchemaRow(bool added, params KeyValuePair<string, string>[] cells)
        {
            foreach (var cell in cells) values[cell.Key] = cell.Value ?? "";
            if (!added) original = new Dictionary<string, string>(values, StringComparer.Ordinal);
        }

        /// <summary>탭 번호: 0 컬럼, 1 인덱스, 2 제약 조건.</summary>
        public abstract int Section { get; }
        /// <summary>오류 목록에 쓰는 종류 이름.</summary>
        public abstract string Kind { get; }

        public bool IsNew => original == null;
        public bool Deleted { get => deleted; set { if (deleted == value) return; deleted = value; if (value) ClearErrors(); Notify(nameof(Deleted)); } }
        public string State => Deleted ? "삭제" : IsNew ? "추가" : values.Keys.Any(Changed) ? "수정" : "";
        public bool Dirty => State != "";
        /// <summary>행 머리 표시(✚ 추가, ✎ 수정, ✕ 삭제).</summary>
        public string Mark => Deleted ? "✕" : IsNew ? "✚" : Dirty ? "✎" : "";

        public string Name { get => Get(nameof(Name)); set => Set(nameof(Name), value); }
        /// <summary>DB에 있는 이름(새 행이면 지금 이름).</summary>
        public string OriginalName => original == null ? Name : Original(nameof(Name));

        protected string Get(string key) => values.TryGetValue(key, out var value) ? value : "";
        protected void Set(string key, string value)
        {
            value = value ?? "";
            if (Get(key) == value) return;
            values[key] = value;
            if (errors.Remove(key)) ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(key));
            Notify(key);
        }
        protected string Original(string key) => original != null && original.TryGetValue(key, out var value) ? value : "";
        protected bool Changed(string key) => original == null || Get(key) != Original(key);

        /// <summary>칸 하나의 원래 값을 지금 값으로(그 DDL이 성공했을 때).</summary>
        protected void Accept(string key) { if (original != null) { original[key] = Get(key); Notify(key); } }
        /// <summary>새로 만든 것이 DB에 생겼을 때. pending 칸은 아직 반영 전 값(원래 값)으로 둔다.</summary>
        protected void Created(params KeyValuePair<string, string>[] pending)
        {
            original = new Dictionary<string, string>(values, StringComparer.Ordinal);
            foreach (var cell in pending) original[cell.Key] = cell.Value;
            Notify("");
        }
        /// <summary>DB에서 지웠을 때(다시 만들기 전 DROP 성공) — 다음 저장은 생성만 다시 한다.</summary>
        protected void Dropped() { original = null; Notify(""); }

        public void Revert()
        {
            if (original != null) foreach (var pair in original) values[pair.Key] = pair.Value;
            deleted = false; ClearErrors(); Notify("");
        }

        /// <summary>이 행만 보고 알 수 있는 오류. 행 사이 규칙(이름 중복·기본 키 하나 등)은 SchemaPlan.Validate가 본다.</summary>
        public abstract IEnumerable<SchemaError> Validate(SchemaContext context);
        /// <summary>검증을 통과했다는 전제에서 실행할 DDL. remove는 삭제가 끝난 행을 표에서 뺀다.</summary>
        public abstract IEnumerable<SchemaStep> Plan(SchemaContext context, Action remove);

        protected SchemaError Error(string field, string message) => new SchemaError { Row = this, Field = field, Message = message };
        protected static SchemaStep Step(int phase, string sql, Action applied, bool destructive = false)
            => new SchemaStep { Phase = phase, Sql = sql, Applied = applied, Destructive = destructive };
        protected static KeyValuePair<string, string> Cell(string key, string value) => new KeyValuePair<string, string>(key, value);
        protected static bool IsIdentifier(string text) => !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);
        /// <summary>이름 검사: 비었거나 제어 문자가 있거나 DBMS의 최대 길이를 넘으면 오류.</summary>
        protected SchemaError NameError(SchemaContext context, string what)
        {
            if (!IsIdentifier(Name)) return Error(nameof(Name), what + " 이름을 입력하세요.");
            if (Name.Trim().Length > context.Ddl.MaxNameLength) return Error(nameof(Name), "이름은 " + context.Ddl.MaxNameLength + "자를 넘을 수 없습니다.");
            return null;
        }
        protected static bool IsFragment(string text) => !(text.Contains(";") || text.Contains("--") || text.Contains("/*") || text.Contains("*/"));

        // 저장 때 검증 결과를 칸에 붙인다(INotifyDataErrorInfo → 칸 빨간 테두리 + 툴팁).
        public bool HasErrors => errors.Count > 0;
        public string ErrorText => string.Join(Environment.NewLine, errors.Values);
        public event EventHandler<DataErrorsChangedEventArgs> ErrorsChanged;
        public IEnumerable GetErrors(string propertyName)
            => !string.IsNullOrEmpty(propertyName) && errors.TryGetValue(propertyName, out var message) ? new[] { message } : Array.Empty<string>();
        public void SetError(string field, string message)
        {
            errors[field] = errors.TryGetValue(field, out var old) ? old + Environment.NewLine + message : message;
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(field));
            Notify(nameof(HasErrors));
        }
        /// <summary>칸 하나의 오류 표시만 지운다(그 칸 편집을 시작할 때).</summary>
        public void ClearError(string field)
        {
            if (string.IsNullOrEmpty(field) || !errors.Remove(field)) return;
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(field));
            Notify(nameof(HasErrors));
        }
        public void ClearErrors()
        {
            if (errors.Count == 0) return;
            var fields = errors.Keys.ToArray(); errors.Clear();
            foreach (var field in fields) ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(field));
            Notify(nameof(HasErrors));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void Notify(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name != "") foreach (var derived in Derived) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(derived));
        }
    }

    /// <summary>컬럼: 이름·자료형·NOT NULL·기본값(SQL 식)·주석. PK 표시는 읽기 전용.</summary>
    internal sealed class ColumnRow : SchemaRow
    {
        public ColumnRow(bool added, string name = "", string dataType = "", bool notNull = false, string defaultValue = "", string comment = "", bool primaryKey = false)
            : base(added, Cell(nameof(Name), name), Cell(nameof(DataType), dataType), Cell(nameof(Nullable), notNull ? "N" : "Y"), Cell(nameof(Default), defaultValue), Cell(nameof(Comment), comment))
        { PrimaryKey = primaryKey; }

        public override int Section => 0;
        public override string Kind => "컬럼";
        public string DataType { get => Get(nameof(DataType)); set => Set(nameof(DataType), value); }
        /// <summary>Oracle NULLABLE 표기(Y/N). 화면은 NotNull 체크박스로 고친다.</summary>
        public string Nullable { get => Get(nameof(Nullable)); set { Set(nameof(Nullable), value); Notify(nameof(NotNull)); } }
        public bool NotNull { get => Nullable == "N"; set => Nullable = value ? "N" : "Y"; }
        public string Default { get => Get(nameof(Default)); set => Set(nameof(Default), value); }
        public string Comment { get => Get(nameof(Comment)); set => Set(nameof(Comment), value); }
        public bool PrimaryKey { get; }

        public override IEnumerable<SchemaError> Validate(SchemaContext context)
        {
            var name = NameError(context, "컬럼"); if (name != null) yield return name;
            if (string.IsNullOrWhiteSpace(DataType)) { if (Changed(nameof(DataType))) yield return Error(nameof(DataType), "자료형을 입력하세요."); }
            else if (!IsFragment(DataType)) yield return Error(nameof(DataType), "자료형에 ; 나 주석(--, /* */)을 쓸 수 없습니다.");
            if (!IsFragment(Default)) yield return Error(nameof(Default), "기본값에 ; 나 주석(--, /* */)을 쓸 수 없습니다.");
        }

        ColumnSpec Spec() => new ColumnSpec { Name = Name.Trim(), DataType = DataType.Trim(), NotNull = NotNull, Default = Default.Trim() };

        public override IEnumerable<SchemaStep> Plan(SchemaContext context, Action remove)
        {
            var ddl = context.Ddl; var target = context.Target;
            if (Deleted)
            {
                if (!IsNew) yield return Step(2, ddl.DropColumn(target, OriginalName), remove, destructive: true);
                yield break;
            }
            var spec = Spec();
            if (IsNew)
            {
                // 주석은 따로 실행한다: 컬럼만 만들어지고 주석이 실패하면 다음 저장은 주석만 다시 한다.
                var hasComment = Comment != "";
                yield return Step(2, ddl.AddColumn(target, spec), () => Created(hasComment ? new[] { Cell(nameof(Comment), "") } : new KeyValuePair<string, string>[0]));
                if (hasComment) yield return Step(2, ddl.CommentColumn(target, spec.Name, Comment), () => Accept(nameof(Comment)));
                yield break;
            }
            if (Changed(nameof(Name))) yield return Step(2, ddl.RenameColumn(target, OriginalName, spec.Name), () => Accept(nameof(Name)));
            if (Changed(nameof(DataType))) yield return Step(2, ddl.ChangeType(target, spec), () => Accept(nameof(DataType)));
            if (Changed(nameof(Default))) yield return Step(2, ddl.ChangeDefault(target, spec), () => Accept(nameof(Default)));
            if (Changed(nameof(Nullable))) yield return Step(2, ddl.ChangeNullability(target, spec), () => Accept(nameof(Nullable)));
            if (Changed(nameof(Comment))) yield return Step(2, ddl.CommentColumn(target, spec.Name, Comment), () => Accept(nameof(Comment)));
        }
    }

    /// <summary>인덱스: 이름·고유·컬럼 목록("A, B DESC"). 종류·상태는 읽기 전용. 고유·컬럼을 바꾸면 DROP 후 CREATE.</summary>
    internal sealed class IndexRow : SchemaRow
    {
        public IndexRow(bool added, string name = "", bool unique = false, string columns = "", string indexType = "", string status = "", bool recreatable = true, string ownerConstraint = null)
            : base(added, Cell(nameof(Name), name), Cell(nameof(Uniqueness), unique ? "UNIQUE" : ""), Cell(nameof(Columns), columns))
        { IndexType = indexType ?? ""; Status = status ?? ""; Recreatable = recreatable; OwnerConstraint = string.IsNullOrEmpty(ownerConstraint) ? null : ownerConstraint; }

        public override int Section => 1;
        public override string Kind => "인덱스";
        public string Uniqueness { get => Get(nameof(Uniqueness)); set { Set(nameof(Uniqueness), value); Notify(nameof(Unique)); } }
        public bool Unique { get => Uniqueness == "UNIQUE"; set => Uniqueness = value ? "UNIQUE" : ""; }
        public string Columns { get => Get(nameof(Columns)); set => Set(nameof(Columns), value); }
        public string IndexType { get; }
        public string Status { get; }
        /// <summary>컬럼·고유를 바꿔 DROP 후 CREATE해도 되는 보통 인덱스인지(TableDescription에서 DBMS가 정함).</summary>
        public bool Recreatable { get; }
        /// <summary>이 인덱스를 쓰는 기본 키·고유 제약 이름(없으면 null). 그 제약을 함께 지워야 인덱스를 지울 수 있다.</summary>
        public string OwnerConstraint { get; }
        public bool ConstraintIndex => OwnerConstraint != null;
        bool Redefined => Changed(nameof(Uniqueness)) || Changed(nameof(Columns));

        public override IEnumerable<SchemaError> Validate(SchemaContext context)
        {
            var name = NameError(context, "인덱스"); if (name != null) yield return name;
            if (!Redefined) yield break;
            if (!IsNew && !Recreatable)
            {
                yield return Error(nameof(Columns), (ConstraintIndex ? "제약 조건 " + OwnerConstraint + "이(가) 쓰는 인덱스는 제약 조건 탭에서 바꾸세요." : IndexType + " 인덱스는 특수 속성을 보존할 수 없어 컬럼·고유 변경을 지원하지 않습니다.")
                    + " 되돌리거나 삭제 후 새로 만드세요.");
                yield break;
            }
            var problem = SchemaPlan.ColumnProblem(Columns, context, allowDescending: true);
            if (problem != null) yield return Error(nameof(Columns), problem);
        }

        public override IEnumerable<SchemaStep> Plan(SchemaContext context, Action remove)
        {
            var ddl = context.Ddl; var target = context.Target;
            if (Deleted)
            {
                if (!IsNew) yield return Step(1, ddl.DropIndex(target, OriginalName), remove);
                yield break;
            }
            var name = Name.Trim();
            if (IsNew || Redefined)
            {
                if (!IsNew) yield return Step(1, ddl.DropIndex(target, OriginalName), Dropped);
                yield return Step(3, ddl.CreateIndex(target, name, Unique, IndexColumnsLogic.Parse(Columns, context.LiveColumns)), () => Created());
            }
            else if (Changed(nameof(Name))) yield return Step(3, ddl.RenameIndex(target, OriginalName, name), () => Accept(nameof(Name)));
        }
    }

    /// <summary>제약 조건: 이름·종류(P/U/R/C)·컬럼·참조(R)·CHECK 조건(C)·상태. 정의를 바꾸면 DROP 후 ADD.</summary>
    internal sealed class ConstraintRow : SchemaRow
    {
        public static readonly string[] Types = { "P", "U", "R", "C" };

        public ConstraintRow(bool added, string name = "", string type = "P", string columns = "", string reference = "", string status = "ENABLED", string condition = "")
            : base(added, Cell(nameof(Name), name), Cell(nameof(Type), type), Cell(nameof(Columns), columns), Cell(nameof(Reference), reference), Cell(nameof(Status), status), Cell(nameof(Condition), condition))
        { }

        public override int Section => 2;
        public override string Kind => "제약 조건";
        public string Type { get => Get(nameof(Type)); set { Set(nameof(Type), value); Notify(nameof(UsesColumns)); Notify(nameof(UsesReference)); Notify(nameof(UsesCondition)); Notify(nameof(CanToggle)); } }
        public string Columns { get => Get(nameof(Columns)); set => Set(nameof(Columns), value); }
        /// <summary>외래 키 참조 대상: "OWNER.TABLE (COL, ...)".</summary>
        public string Reference { get => Get(nameof(Reference)); set => Set(nameof(Reference), value); }
        public string Status { get => Get(nameof(Status)); set { Set(nameof(Status), value); Notify(nameof(Enabled)); } }
        public bool Enabled { get => Status != "DISABLED"; set => Status = value ? "ENABLED" : "DISABLED"; }
        public string Condition { get => Get(nameof(Condition)); set => Set(nameof(Condition), value); }
        public bool UsesColumns => Type != "C";
        public bool UsesReference => Type == "R";
        public bool UsesCondition => Type == "C";
        /// <summary>이 DBMS에서 이 종류 제약을 끄고 켤 수 있는지(편집 창이 ISchemaDdl.CanDisable을 넣는다. 없으면 모두 가능).</summary>
        public Func<string, bool> DisableRule { get => disableRule; set { disableRule = value; Notify(nameof(CanToggle)); } }
        Func<string, bool> disableRule;
        public bool CanToggle => disableRule == null || disableRule(Type);
        bool Redefined => Changed(nameof(Type)) || Changed(nameof(Columns)) || Changed(nameof(Reference)) || Changed(nameof(Condition));

        public override IEnumerable<SchemaError> Validate(SchemaContext context)
        {
            var name = NameError(context, "제약 조건"); if (name != null) yield return name;
            if (!Types.Contains(Type)) { yield return Error(nameof(Type), "종류를 고르세요(기본 키·고유·외래 키·검사)."); yield break; }
            if (!Enabled && (Changed(nameof(Status)) || Changed(nameof(Type))) && !context.Ddl.CanDisable(Type))
                yield return Error(nameof(Status), "이 DBMS에서는 이 종류의 제약 조건을 끌 수 없습니다. [사용]을 켜세요.");
            if (!Redefined) yield break;
            if (UsesColumns)
            {
                var problem = SchemaPlan.ColumnProblem(Columns, context, allowDescending: false);
                if (problem != null) yield return Error(nameof(Columns), problem);
            }
            if (UsesReference)
            {
                if (string.IsNullOrWhiteSpace(Reference)) yield return Error(nameof(Reference), "참조할 테이블을 입력하세요(예: OWNER.PARENT (ID)).");
                else if (!IsFragment(Reference)) yield return Error(nameof(Reference), "참조에 ; 나 주석(--, /* */)을 쓸 수 없습니다.");
            }
            if (UsesCondition)
            {
                if (string.IsNullOrWhiteSpace(Condition)) yield return Error(nameof(Condition), "CHECK 조건을 입력하세요(예: AMOUNT >= 0).");
                else if (!IsFragment(Condition)) yield return Error(nameof(Condition), "조건에 ; 나 주석(--, /* */)을 쓸 수 없습니다.");
            }
        }

        ConstraintSpec Spec(SchemaContext context) => new ConstraintSpec
        {
            Type = Type,
            Columns = UsesColumns ? IndexColumnsLogic.Parse(Columns, context.LiveColumns) : new List<IndexColumnChoice>(),
            Reference = Reference.Trim(),
            Condition = Condition.Trim()
        };

        public override IEnumerable<SchemaStep> Plan(SchemaContext context, Action remove)
        {
            var ddl = context.Ddl; var target = context.Target;
            if (Deleted)
            {
                if (!IsNew) yield return Step(0, ddl.DropConstraint(target, OriginalName), remove);
                yield break;
            }
            var name = Name.Trim();
            string baseline;
            if (IsNew || Redefined)
            {
                if (!IsNew) yield return Step(0, ddl.DropConstraint(target, OriginalName), Dropped);
                // 새로 만든 제약은 켜진 상태 — 꺼 둘 거면 다음 단계에서 끈다.
                yield return Step(4, ddl.AddConstraint(target, name, Spec(context)), () => Created(Cell(nameof(Status), "ENABLED")));
                baseline = "ENABLED";
            }
            else
            {
                if (Changed(nameof(Name))) yield return Step(4, ddl.RenameConstraint(target, OriginalName, name), () => Accept(nameof(Name)));
                baseline = Original(nameof(Status));
            }
            if (Status != baseline)
                yield return Step(5, ddl.SetConstraintEnabled(target, name, Type, Enabled), () => Accept(nameof(Status)));
        }
    }

    /// <summary>저장 시 검증(모든 오류를 한꺼번에)과 DDL 순서 정하기.</summary>
    internal static class SchemaPlan
    {
        /// <summary>조회한 테이블 정보를 편집 행으로(모두 원래 값 = DB 값). ddl이 있으면 제약 끄기 규칙도 붙인다.</summary>
        public static SchemaContext Load(TableDescription info, ISchemaDdl ddl)
        {
            var context = new SchemaContext { Owner = info.Owner, Table = info.Name, Ddl = ddl ?? new OracleSchemaDdl() };
            foreach (var c in info.Columns)
                context.Columns.Add(new ColumnRow(false, c.Column.Name, c.Column.TypeLabel, !c.Column.Nullable, c.Default, c.Comment, c.Column.PrimaryKey));
            foreach (var i in info.Indexes)
                context.Indexes.Add(new IndexRow(false, i.Name, i.Unique, i.Columns, i.Type, i.Status, i.Recreatable, i.OwnerConstraint));
            foreach (var c in info.Constraints)
            {
                var reference = c.Reference == null ? "" : c.Reference + (c.ReferenceColumns.Count > 0 ? " (" + string.Join(", ", c.ReferenceColumns) + ")" : "");
                var row = new ConstraintRow(false, c.Name, c.Type, c.Columns, reference, string.IsNullOrEmpty(c.Status) ? "ENABLED" : c.Status, c.Condition);
                if (ddl != null) row.DisableRule = ddl.CanDisable;
                context.Constraints.Add(row);
            }
            return context;
        }

        /// <summary>모든 행의 오류를 지우고 다시 검사해 칸에 붙인다. 오류가 없으면 빈 목록.</summary>
        public static List<SchemaError> Validate(SchemaContext context)
        {
            foreach (var row in context.All) row.ClearErrors();
            var found = new List<SchemaError>();
            foreach (var row in context.All.Where(r => r.Dirty && !r.Deleted)) found.AddRange(row.Validate(context));

            // 같은 탭에서 이름 중복(지울 행 제외, 앞뒤 공백 무시)
            foreach (var section in new IEnumerable<SchemaRow>[] { context.Columns, context.Indexes, context.Constraints })
                foreach (var group in section.Where(r => !r.Deleted && !string.IsNullOrWhiteSpace(r.Name)).GroupBy(r => r.Name.Trim(), StringComparer.Ordinal).Where(g => g.Count() > 1))
                    foreach (var row in group.Where(r => r.Dirty))
                        found.Add(new SchemaError { Row = row, Field = nameof(SchemaRow.Name), Message = "이름 '" + group.Key + "'이(가) 중복되었습니다." });

            // 기본 키는 하나
            var keys = context.Constraints.Where(c => !c.Deleted && c.Type == "P").ToList();
            if (keys.Count > 1)
                foreach (var key in keys.Where(k => k.Dirty))
                    found.Add(new SchemaError { Row = key, Field = nameof(ConstraintRow.Type), Message = "기본 키는 하나만 둘 수 있습니다. 기존 기본 키를 삭제하거나 고유(U)로 바꾸세요." });

            // 지울 컬럼을 남는 인덱스·제약이 아직 쓰는지(Oracle은 다중 컬럼 제약이 걸린 컬럼 삭제를 거부한다)
            var dropped = context.Columns.Where(c => c.Deleted && !c.IsNew).Select(c => c.OriginalName).ToArray();
            if (dropped.Length > 0)
            {
                var everything = context.Columns.Select(c => c.OriginalName).Concat(context.LiveColumns).Distinct(StringComparer.Ordinal).ToArray();
                foreach (var column in context.Columns.Where(c => c.Deleted && !c.IsNew))
                {
                    var users = context.Indexes.Where(i => !i.Deleted).Select(i => new { i.Name, i.Columns })
                        .Concat(context.Constraints.Where(c => !c.Deleted && c.UsesColumns).Select(c => new { c.Name, c.Columns }))
                        .Where(u => Uses(u.Columns, column.OriginalName, everything)).Select(u => u.Name).ToArray();
                    if (users.Length > 0)
                        found.Add(new SchemaError { Row = column, Field = nameof(SchemaRow.Name), Message = "삭제할 컬럼을 " + string.Join(", ", users) + "이(가) 사용합니다. 먼저 그 인덱스·제약을 삭제하거나 수정하세요." });
                }
            }
            // 제약이 쓰는 인덱스는 DROP INDEX로 지울 수 없다(그 제약을 함께 지우면 제약이 먼저 지워진다 — 실행 순서 0 → 1).
            foreach (var index in context.Indexes.Where(i => i.Deleted && !i.IsNew && i.ConstraintIndex))
                if (!context.Constraints.Any(c => c.Deleted && !c.IsNew && string.Equals(c.OriginalName, index.OwnerConstraint, StringComparison.Ordinal)))
                    found.Add(new SchemaError { Row = index, Field = nameof(SchemaRow.Name), Message = "제약 조건 " + index.OwnerConstraint + "이(가) 이 인덱스를 씁니다. 그 제약을 함께 지우거나 인덱스 삭제를 취소하세요." });
            foreach (var error in found) error.Row.SetError(error.Field, error.Message);
            return found;
        }

        /// <summary>검증을 통과한 변경의 DDL을 실행 순서(Phase, 같은 단계는 표 순서)대로.</summary>
        public static List<SchemaStep> Build(SchemaContext context, Action<SchemaRow> remove)
        {
            return context.All.Where(r => r.Dirty).SelectMany(row => row.Plan(context, () => remove(row))).OrderBy(step => step.Phase).ToList();
        }

        /// <summary>인덱스·키 컬럼 목록의 문제(없으면 null).</summary>
        public static string ColumnProblem(string text, SchemaContext context, bool allowDescending)
        {
            if (string.IsNullOrWhiteSpace(text)) return "컬럼을 하나 이상 고르세요.";
            try
            {
                var columns = IndexColumnsLogic.Parse(text, context.LiveColumns);
                if (columns.Count == 0) return "컬럼을 하나 이상 고르세요.";
                if (!allowDescending && columns.Any(c => c.Descending)) return "제약 조건 컬럼에는 DESC를 쓸 수 없습니다.";
                return null;
            }
            catch (ArgumentException ex) { return ex.Message; }
        }

        static bool Uses(string columns, string name, string[] names)
        {
            try { return IndexColumnsLogic.Parse(columns, names).Any(c => c.Name == name); }
            catch (ArgumentException) { return false; } // 표현식 인덱스 등은 판단하지 않는다(DB가 알려 준다).
        }
    }
}
