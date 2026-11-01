using System;
using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    public class SchemaRowTests
    {
        static SchemaContext Context(IEnumerable<ColumnRow> columns = null, IEnumerable<IndexRow> indexes = null, IEnumerable<ConstraintRow> constraints = null, ISchemaDdl ddl = null)
        {
            return new SchemaContext
            {
                Owner = "O", Table = "T", Ddl = ddl ?? new OracleSchemaDdl(),
                Columns = (columns ?? new[] { new ColumnRow(false, "A", "NUMBER"), new ColumnRow(false, "B", "NUMBER") }).ToList(),
                Indexes = (indexes ?? Array.Empty<IndexRow>()).ToList(),
                Constraints = (constraints ?? Array.Empty<ConstraintRow>()).ToList()
            };
        }

        static List<SchemaStep> Steps(SchemaContext context) => SchemaPlan.Build(context, row => { });

        [Fact]
        public void ValidationCollectsAllErrorsAtOnceAndMarksCells()
        {
            var blank = new ColumnRow(true);
            var typed = new ColumnRow(true, "CODE", "VARCHAR2(10); DROP TABLE X");
            var index = new IndexRow(true, "IX_T", false, "");
            var check = new ConstraintRow(true, "CK_T", "C");
            var context = Context(new[] { new ColumnRow(false, "A", "NUMBER"), blank, typed }, new[] { index }, new[] { check });

            var errors = SchemaPlan.Validate(context);

            Assert.Contains(errors, e => e.Row == blank && e.Field == nameof(SchemaRow.Name));
            Assert.Contains(errors, e => e.Row == blank && e.Field == nameof(ColumnRow.DataType));
            Assert.Contains(errors, e => e.Row == typed && e.Field == nameof(ColumnRow.DataType));
            Assert.Contains(errors, e => e.Row == index && e.Field == nameof(IndexRow.Columns));
            Assert.Contains(errors, e => e.Row == check && e.Field == nameof(ConstraintRow.Condition));
            Assert.True(blank.HasErrors);
            Assert.Single(blank.GetErrors(nameof(ColumnRow.DataType)).Cast<object>());

            // 그 칸을 고치면 그 칸의 오류 표시만 사라진다.
            blank.DataType = "NUMBER";
            Assert.Empty(blank.GetErrors(nameof(ColumnRow.DataType)).Cast<object>());
            Assert.True(blank.HasErrors);
        }

        [Fact]
        public void EditingDoesNotValidateUntilSave()
        {
            var row = new ColumnRow(true);
            row.Name = "";
            row.DataType = "";
            Assert.False(row.HasErrors);
            Assert.Equal("추가", row.State);
        }

        [Fact]
        public void DuplicateNamesAndSecondPrimaryKeyAreReported()
        {
            var first = new ColumnRow(false, "A", "NUMBER");
            var copy = new ColumnRow(true, "A ", "NUMBER");
            var oldKey = new ConstraintRow(false, "PK_T", "P", "A");
            var newKey = new ConstraintRow(true, "PK_T2", "P", "\"B\"");
            var context = Context(new[] { first, copy, new ColumnRow(false, "B", "NUMBER") }, null, new[] { oldKey, newKey });

            var errors = SchemaPlan.Validate(context);

            Assert.Contains(errors, e => e.Row == copy && e.Field == nameof(SchemaRow.Name));
            Assert.DoesNotContain(errors, e => e.Row == first); // 바꾸지 않은 행에는 붙이지 않는다
            Assert.Contains(errors, e => e.Row == newKey && e.Field == nameof(ConstraintRow.Type));

            oldKey.Deleted = true;
            copy.Name = "C";
            Assert.Empty(SchemaPlan.Validate(context));
        }

        [Fact]
        public void DroppingColumnStillUsedByIndexIsReported()
        {
            var a = new ColumnRow(false, "A", "NUMBER");
            var index = new IndexRow(false, "IX_T", false, "A, B");
            var context = Context(new[] { a, new ColumnRow(false, "B", "NUMBER") }, new[] { index });
            a.Deleted = true;
            var error = Assert.Single(SchemaPlan.Validate(context));
            Assert.Same(a, error.Row);
            Assert.Contains("IX_T", error.Message);

            index.Deleted = true;
            Assert.Empty(SchemaPlan.Validate(context));
        }

        [Fact]
        public void NewColumn_AddThenCommentRetriesOnlyComment()
        {
            var row = new ColumnRow(true, "CODE", "VARCHAR2(30 CHAR)", notNull: true, defaultValue: "'A'", comment: "코드");
            var context = Context(new[] { row });
            Assert.Empty(SchemaPlan.Validate(context));
            var steps = Steps(context);
            Assert.Equal(2, steps.Count);
            Assert.Contains("ADD (\"CODE\" VARCHAR2(30 CHAR) DEFAULT 'A' NOT NULL)", steps[0].Sql);
            steps[0].Applied(); // 주석이 실패한 상황
            Assert.Equal("수정", row.State);
            var retry = Assert.Single(Steps(context));
            Assert.StartsWith("COMMENT ON COLUMN", retry.Sql);
            retry.Applied();
            Assert.False(row.Dirty);
        }

        [Fact]
        public void ExistingColumn_DiffAndDelete()
        {
            var row = new ColumnRow(false, "CODE", "VARCHAR2(30 CHAR)", false, "'A'", "old");
            row.Name = "NEW_CODE"; row.DataType = "VARCHAR2(60 CHAR)"; row.NotNull = true; row.Default = ""; row.Comment = "new";
            Assert.Equal("수정", row.State);
            var context = Context(new[] { row });
            var steps = Steps(context);
            Assert.Equal(5, steps.Count);
            Assert.Contains("RENAME COLUMN \"CODE\" TO \"NEW_CODE\"", steps[0].Sql);
            Assert.Contains("DEFAULT NULL", steps[2].Sql);
            foreach (var step in steps) step.Applied();
            Assert.False(row.Dirty);

            bool removed = false;
            row.Deleted = true;
            var deletion = Assert.Single(SchemaPlan.Build(context, r => removed = true));
            Assert.Contains("DROP COLUMN \"NEW_CODE\"", deletion.Sql);
            Assert.True(deletion.Destructive);
            deletion.Applied();
            Assert.True(removed);
        }

        [Fact]
        public void NotNullCheckboxMapsToOracleFlag()
        {
            var column = new ColumnRow(false, "ID", "NUMBER");
            column.NotNull = true; Assert.Equal("N", column.Nullable); Assert.Equal("수정", column.State);
            column.NotNull = false; Assert.False(column.Dirty);
            var index = new IndexRow(true, "IX", false, "ID");
            index.Unique = true; Assert.Equal("UNIQUE", index.Uniqueness);
        }

        [Fact]
        public void StepsRunInDependencyOrder()
        {
            var dropKey = new ConstraintRow(false, "PK_OLD", "P", "A"); dropKey.Deleted = true;
            var dropIndex = new IndexRow(false, "IX_OLD", false, "A"); dropIndex.Deleted = true;
            var newColumn = new ColumnRow(true, "C", "NUMBER");
            var newIndex = new IndexRow(true, "IX_C", false, "\"C\"");
            var newKey = new ConstraintRow(true, "PK_T", "P", "\"A\", \"C\"");
            // 표 순서와 반대로 넣어도 삭제 → 컬럼 → 인덱스 → 제약 순서다.
            var context = Context(new[] { new ColumnRow(false, "A", "NUMBER"), newColumn }, new[] { newIndex, dropIndex }, new[] { newKey, dropKey });
            Assert.Empty(SchemaPlan.Validate(context));
            var sql = Steps(context).Select(s => s.Sql).ToArray();
            Assert.StartsWith("ALTER TABLE \"O\".\"T\" DROP CONSTRAINT \"PK_OLD\"", sql[0]);
            Assert.StartsWith("DROP INDEX", sql[1]);
            Assert.Contains("ADD (\"C\" NUMBER)", sql[2]);
            Assert.StartsWith("CREATE INDEX \"O\".\"IX_C\" ON \"O\".\"T\" (\"C\")", sql[3]);
            Assert.Contains("ADD CONSTRAINT \"PK_T\" PRIMARY KEY (\"A\", \"C\")", sql[4]);
        }

        [Fact]
        public void ForeignKeyAndCheckDefinitions()
        {
            var foreign = new ConstraintRow(true, "FK_T", "R", "A, B", "PARENT (ID, LINE)");
            var check = new ConstraintRow(true, "CK_T", "C", "", "", "ENABLED", "A >= 0");
            var context = Context(constraints: new[] { foreign, check });
            Assert.Empty(SchemaPlan.Validate(context));
            var steps = Steps(context);
            Assert.Contains("FOREIGN KEY (\"A\", \"B\") REFERENCES PARENT (ID, LINE)", steps[0].Sql);
            Assert.Contains("CHECK (A >= 0)", steps[1].Sql);

            var missing = new ConstraintRow(true, "FK_X", "R", "A");
            var descending = new ConstraintRow(true, "UK_X", "U", "A DESC");
            var errors = SchemaPlan.Validate(Context(constraints: new[] { missing, descending }));
            Assert.Contains(errors, e => e.Row == missing && e.Field == nameof(ConstraintRow.Reference));
            Assert.Contains(errors, e => e.Row == descending && e.Field == nameof(ConstraintRow.Columns));
        }

        [Fact]
        public void DisabledNewConstraintIsAddedThenDisabled()
        {
            var key = new ConstraintRow(true, "UK_T", "U", "A", "", "DISABLED");
            var context = Context(constraints: new[] { key });
            var steps = Steps(context);
            Assert.Equal(2, steps.Count);
            Assert.Contains("DISABLE CONSTRAINT \"UK_T\"", steps[1].Sql);
            steps[0].Applied();
            Assert.Equal("수정", key.State); // 비활성화만 남았다
            steps[1].Applied();
            Assert.False(key.Dirty);
        }

        [Fact]
        public void ExistingCheckConstraintCanChangeStatusWithoutCondition()
        {
            var row = new ConstraintRow(false, "CK_T", "C", "A", "", "ENABLED", "");
            row.Enabled = false;
            var context = Context(constraints: new[] { row });
            Assert.Empty(SchemaPlan.Validate(context));
            Assert.Contains("DISABLE CONSTRAINT", Assert.Single(Steps(context)).Sql);
        }

        [Fact]
        public void SpecialIndexesCannotBeRecreatedButCanBeRenamed()
        {
            foreach (var kind in new[] { "BITMAP", "FUNCTION-BASED NORMAL", "DOMAIN" })
            {
                var row = new IndexRow(false, "IX", false, "A", kind, recreatable: false);
                row.Columns = "B";
                var context = Context(indexes: new[] { row });
                Assert.Contains(SchemaPlan.Validate(context), e => e.Row == row && e.Field == nameof(IndexRow.Columns));
                row.Revert(); row.Name = "IX_NEW";
                Assert.Empty(SchemaPlan.Validate(context));
                Assert.Contains("RENAME TO \"IX_NEW\"", Assert.Single(Steps(context)).Sql);
            }
        }

        [Fact]
        public void IndexRecreateFailureRetriesOnlyCreation()
        {
            var row = new IndexRow(false, "IX_T", false, "A");
            row.Columns = "A, B DESC";
            var context = Context(indexes: new[] { row });
            var steps = Steps(context);
            Assert.Equal(2, steps.Count);
            Assert.Contains("(\"A\", \"B\" DESC)", steps[1].Sql);
            steps[0].Applied();
            Assert.True(row.IsNew);
            var retry = Assert.Single(Steps(context));
            Assert.StartsWith("CREATE INDEX", retry.Sql);
            retry.Applied();
            Assert.False(row.Dirty);
            row.Columns = "A"; row.Deleted = true; row.Revert();
            Assert.Equal("A, B DESC", row.Columns);
            Assert.False(row.Dirty);
        }

        [Fact]
        public void ConstraintMetadataIncludesCheckAndReferencedColumns()
        {
            var table = new System.Data.DataTable();
            foreach (var name in new[] { "CONSTRAINT_NAME", "CONSTRAINT_TYPE", "STATUS", "COLS", "R_OWNER", "R_TABLE", "INFO_R_COLS", "CHECK_CONDITION" }) table.Columns.Add(name);
            table.Rows.Add("FK_T", "R", "ENABLED", "A, B", "O", "PARENT", "ID\u001fLINE", DBNull.Value);
            using (var reader = table.CreateDataReader())
            {
                reader.Read(); var result = TableInfo.ReadConstraint(reader);
                Assert.Equal(new[] { "ID", "LINE" }, result.ReferenceColumns);
                Assert.Equal("O.PARENT", result.Reference);
            }
            table.Rows.Clear(); table.Rows.Add("CK_T", "C", "ENABLED", "A", DBNull.Value, DBNull.Value, DBNull.Value, "A > 0");
            using (var reader = table.CreateDataReader()) { reader.Read(); Assert.Equal("A > 0", TableInfo.ReadConstraint(reader).Condition); }
        }
    
        [Fact]
        public void SameEditsProduceDialectSpecificSql()
        {
            ColumnRow Changed() { var c = new ColumnRow(false, "CODE", "int", false); c.NotNull = true; c.Default = "0"; return c; }
            var pg = Steps(Context(new[] { Changed() }, ddl: new PostgresSchemaDdl())).Select(s => s.Sql).ToArray();
            Assert.Contains(pg, s => s.EndsWith("ALTER COLUMN \"CODE\" SET DEFAULT 0"));
            Assert.Contains(pg, s => s.EndsWith("ALTER COLUMN \"CODE\" SET NOT NULL"));
            var ms = Steps(Context(new[] { Changed() }, ddl: new SqlServerSchemaDdl())).Select(s => s.Sql).ToArray();
            Assert.Contains(ms, s => s.EndsWith("ADD DEFAULT (0) FOR [CODE]"));
            Assert.Contains(ms, s => s == "ALTER TABLE [O].[T] ALTER COLUMN [CODE] int NOT NULL");
        }

        [Fact]
        public void DbmsRulesAreCheckedOnSave()
        {
            // PostgreSQL 이름 길이 63, 제약 끄기 불가
            var longName = new ColumnRow(true, new string('a', 64), "integer");
            var key = new ConstraintRow(false, "FK_T", "R", "\"A\"", "\"public\".\"p\" (\"id\")");
            key.Enabled = false;
            var errors = SchemaPlan.Validate(Context(new[] { new ColumnRow(false, "A", "integer"), longName }, constraints: new[] { key }, ddl: new PostgresSchemaDdl()));
            Assert.Contains(errors, e => e.Row == longName && e.Message.Contains("63"));
            Assert.Contains(errors, e => e.Row == key && e.Field == nameof(ConstraintRow.Status));
            // SQL Server는 외래 키는 끌 수 있고 기본 키는 없다
            var fk = new ConstraintRow(false, "FK_T", "R", "\"A\"", "dbo.P (ID)"); fk.Enabled = false;
            var pk = new ConstraintRow(false, "PK_T", "P", "\"A\""); pk.Enabled = false;
            var ms = SchemaPlan.Validate(Context(constraints: new[] { fk, pk }, ddl: new SqlServerSchemaDdl()));
            Assert.DoesNotContain(ms, e => e.Row == fk);
            Assert.Contains(ms, e => e.Row == pk && e.Field == nameof(ConstraintRow.Status));
        }

        [Fact]
        public void ConstraintIndexCannotBeDroppedAlone()
        {
            var index = new IndexRow(false, "PK_T", true, "\"A\"", "btree", "VALID", recreatable: false, ownerConstraint: "PK_T");
            index.Deleted = true;
            var context = Context(indexes: new[] { index }, constraints: new[] { new ConstraintRow(false, "PK_T", "P", "\"A\"") });
            Assert.Contains(SchemaPlan.Validate(context), e => e.Row == index);
            context.Constraints[0].Deleted = true;
            Assert.DoesNotContain(SchemaPlan.Validate(context), e => e.Row == index);
        }
    }
}
