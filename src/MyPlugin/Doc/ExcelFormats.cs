using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MyPlugin
{
    /// <summary>등록한 엑셀 서식 하나: 이름, 관리 폴더 안의 파일 이름, 대상 테이블("OWNER.TABLE" 또는 "TABLE", 비면 모든 결과).</summary>
    internal sealed class ExcelFormatEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        /// <summary>관리 폴더(<see cref="ExcelFormats.Folder"/>) 안의 파일 이름. 기본 서식은 null(파일 없이 결과 열로 만듦).</summary>
        public string File { get; set; }
        public string Table { get; set; } = "";
        public bool BuiltIn => File == null;

        public ExcelFormatEntry Copy() => new ExcelFormatEntry { Id = Id, Name = Name, File = File, Table = Table };
    }

    /// <summary>
    /// 결과 엑셀 서식 등록(설정 창 "엑셀 서식"). 처음 등록 항목은 "기본 서식"(자동 — 결과 열 그대로)이고 지울 수 없다.
    /// 등록한 서식 파일은 플러그인 데이터 폴더의 excel-formats\에 복사해 관리한다(원본을 옮기거나 지워도 남는다).
    /// 설정 값 "resultExcel.formats"에 JSON 목록으로 저장한다. 결과 편집 막대는 <see cref="For"/>로 그 테이블에 쓸 서식을 고른다.
    /// </summary>
    internal static class ExcelFormats
    {
        public const string SettingKey = "resultExcel.formats";
        public const string FolderName = "excel-formats";
        public static readonly ExcelFormatEntry Default = new ExcelFormatEntry { Id = "builtin", Name = "기본 서식 (자동 — 결과 열 그대로)", File = null, Table = "" };

        static List<ExcelFormatEntry> current = new List<ExcelFormatEntry>();
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };

        /// <summary>플러그인 데이터 폴더(설정 페이지가 넣는다). 없으면 등록 서식을 쓸 수 없다.</summary>
        public static string DataDirectory { get; set; }
        public static string Folder => string.IsNullOrEmpty(DataDirectory) ? null : Path.Combine(DataDirectory, FolderName);

        /// <summary>등록한 서식(기본 서식 제외, 등록 순서).</summary>
        public static IReadOnlyList<ExcelFormatEntry> Registered => current;

        /// <summary>등록 목록을 설정에 저장하는 동작(설정 페이지가 넣는다 — 결과 그리드에서 서식을 만들어 등록할 때 쓴다).</summary>
        public static Action<string> Persist { get; set; }

        /// <summary>등록 항목 하나를 더하고 설정에 저장한다.</summary>
        public static void Add(ExcelFormatEntry entry)
        {
            current.Add(entry.Copy());
            Persist?.Invoke(Serialize(current));
        }

        public static void Set(IEnumerable<ExcelFormatEntry> entries) => current = (entries ?? Enumerable.Empty<ExcelFormatEntry>()).Where(e => e != null && !e.BuiltIn).Select(e => e.Copy()).ToList();

        public static List<ExcelFormatEntry> Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<ExcelFormatEntry>();
            try
            {
                return (JsonSerializer.Deserialize<List<ExcelFormatEntry>>(json, Json) ?? new List<ExcelFormatEntry>())
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.File)).ToList();
            }
            catch (JsonException)
            {
                return new List<ExcelFormatEntry>();
            }
        }

        public static string Serialize(IEnumerable<ExcelFormatEntry> entries)
            => JsonSerializer.Serialize(entries.Where(e => !e.BuiltIn).Select(e => new { e.Id, e.Name, e.File, e.Table }), Json);

        /// <summary>이 테이블 결과에 쓸 서식: 기본 서식 먼저, 그다음 대상 테이블이 맞거나(대소문자 무시, 스키마 생략 가능) 대상이 빈 등록 서식.</summary>
        public static List<ExcelFormatEntry> For(string owner, string table, IEnumerable<ExcelFormatEntry> registered = null)
        {
            var list = new List<ExcelFormatEntry> { Default };
            list.AddRange((registered ?? current).Where(e => Matches(e.Table, owner, table)));
            return list;
        }

        public static bool Matches(string target, string owner, string table)
        {
            target = (target ?? "").Trim();
            if (target.Length == 0) return true;
            if (string.IsNullOrEmpty(table)) return false;
            var dot = target.LastIndexOf('.');
            if (dot < 0) return string.Equals(target, table, StringComparison.OrdinalIgnoreCase);
            return string.Equals(target.Substring(dot + 1), table, StringComparison.OrdinalIgnoreCase)
                && string.Equals(target.Substring(0, dot), owner ?? "", StringComparison.OrdinalIgnoreCase);
        }

        public static string PathOf(ExcelFormatEntry entry) => entry == null || entry.BuiltIn || Folder == null ? null : Path.Combine(Folder, entry.File);

        /// <summary>파일을 관리 폴더로 복사해 등록 항목을 만든다(같은 이름이 있으면 " (2)"…). 서식 규칙(이름 정의 DBH_COLUMNS로 지정한 컬럼명 행)을 먼저 확인한다.</summary>
        public static ExcelFormatEntry Import(string sourcePath, string table)
        {
            if (Folder == null) throw new InvalidOperationException("플러그인 데이터 폴더를 알 수 없어 서식을 등록할 수 없습니다.");
            ResultExcel.Inspect(sourcePath);
            Directory.CreateDirectory(Folder);
            var file = UniqueFile(Path.GetFileName(sourcePath));
            System.IO.File.Copy(sourcePath, Path.Combine(Folder, file));
            return new ExcelFormatEntry { Name = Path.GetFileNameWithoutExtension(file), File = file, Table = table ?? "" };
        }

        /// <summary>관리 폴더에 새 파일 이름을 정한다(있으면 " (2)"·" (3)"…).</summary>
        public static string UniqueFile(string name)
        {
            var baseName = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            if (string.IsNullOrEmpty(ext)) ext = ".xlsx";
            foreach (var bad in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(bad, '_');
            var file = baseName + ext;
            for (int n = 2; Folder != null && System.IO.File.Exists(Path.Combine(Folder, file)); n++)
                file = baseName + " (" + n + ")" + ext;
            return file;
        }
    }
}
