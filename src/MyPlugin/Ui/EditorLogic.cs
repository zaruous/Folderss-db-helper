using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MyPlugin
{
    /// <summary>편집기 글 바꾸기 한 번: [Start, Start+Length)를 Replacement로 바꾸고 SelectStart·SelectLength를 고른다.</summary>
    internal sealed class TextEdit
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public string Replacement { get; set; }
        public int SelectStart { get; set; }
        public int SelectLength { get; set; }
    }

    /// <summary>SQL 편집기의 글 다루기(순수 로직, 테스트 대상): 줄 주석 토글, 대·소문자 바꾸기.</summary>
    internal static class EditorLogic
    {
        private const string Comment = "--";

        /// <summary>
        /// 줄 주석 토글(Ctrl+/). 대상은 선택이 걸친 줄들(선택이 없으면 캐럿 줄, 선택이 다음 줄 맨 앞에서 끝나면 그 줄은 빼고).
        /// 비어 있지 않은 대상 줄이 모두 "--"로 시작하면(앞 공백 무시) 그 "--"와 뒤 공백 하나를 지우고,
        /// 아니면 가장 얕은 들여쓰기 위치에 "-- "를 넣는다(빈 줄은 그대로). 줄바꿈 형식은 그대로 둔다.
        /// 선택이 있었으면 바뀐 줄 전체를 고르고, 없었으면 캐럿을 같은 글자 앞에 둔다.
        /// </summary>
        public static TextEdit ToggleLineComment(string text, int selectionStart, int selectionLength)
        {
            text = text ?? "";
            selectionStart = Clamp(selectionStart, 0, text.Length);
            selectionLength = Clamp(selectionLength, 0, text.Length - selectionStart);
            var blockStart = LineStart(text, selectionStart);
            var lastPosition = selectionStart + selectionLength;
            if (selectionLength > 0 && lastPosition > blockStart && IsLineStart(text, lastPosition))
            {
                // 다음 줄 맨 앞에서 끝난 선택: 그 줄은 대상이 아니다(앞 줄의 줄바꿈 "\r\n"을 통째로 건너뛴다)
                lastPosition--;
                if (lastPosition > 0 && text[lastPosition] == '\n' && text[lastPosition - 1] == '\r')
                    lastPosition--;
            }
            var blockEnd = LineEnd(text, lastPosition);

            var lines = SplitKeepingBreaks(text.Substring(blockStart, blockEnd - blockStart));
            var contentLines = lines.Where(l => l.Content.Trim().Length > 0).ToList();
            if (contentLines.Count == 0)
                return new TextEdit { Start = blockStart, Length = 0, Replacement = "", SelectStart = selectionStart, SelectLength = selectionLength };

            var uncomment = contentLines.All(l => l.Content.TrimStart().StartsWith(Comment, StringComparison.Ordinal));
            var indent = contentLines.Min(l => l.Content.Length - l.Content.TrimStart().Length);
            var sb = new StringBuilder();
            var caretShift = 0;
            var caretLineStart = blockStart;
            var offset = blockStart;
            foreach (var line in lines)
            {
                var content = line.Content;
                var changed = content;
                var shift = 0;
                if (content.Trim().Length > 0)
                {
                    if (uncomment)
                    {
                        var at = content.Length - content.TrimStart().Length;
                        var remove = Comment.Length + (at + Comment.Length < content.Length && content[at + Comment.Length] == ' ' ? 1 : 0);
                        changed = content.Remove(at, remove);
                        shift = -remove;
                    }
                    else
                    {
                        changed = content.Insert(indent, Comment + " ");
                        shift = Comment.Length + 1;
                    }
                }
                if (selectionStart >= offset && selectionStart <= offset + content.Length)
                {
                    caretLineStart = offset;
                    caretShift = shift;
                }
                sb.Append(changed).Append(line.Break);
                offset += content.Length + line.Break.Length;
            }
            var replacement = sb.ToString();
            var edit = new TextEdit { Start = blockStart, Length = blockEnd - blockStart, Replacement = replacement };
            if (selectionLength > 0)
            {
                edit.SelectStart = blockStart;
                edit.SelectLength = replacement.Length;
            }
            else
            {
                // 캐럿은 같은 글자 앞에(지운 주석 안에 있었으면 줄의 들여쓰기 끝으로)
                edit.SelectStart = Math.Max(caretLineStart, selectionStart + caretShift);
                edit.SelectLength = 0;
            }
            return edit;
        }

        /// <summary>선택한 글의 대·소문자 바꾸기(Ctrl+Shift+U·Ctrl+Shift+L). 문화권과 무관하게(Invariant) 바꾼다.</summary>
        public static string ChangeCase(string text, bool upper)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? "";
            return upper ? text.ToUpperInvariant() : text.ToLowerInvariant();
        }

        private struct Line
        {
            public string Content;
            public string Break;
        }

        private static List<Line> SplitKeepingBreaks(string block)
        {
            var lines = new List<Line>();
            var start = 0;
            for (var i = 0; i < block.Length; i++)
            {
                if (block[i] != '\r' && block[i] != '\n')
                    continue;
                var breakLength = block[i] == '\r' && i + 1 < block.Length && block[i + 1] == '\n' ? 2 : 1;
                lines.Add(new Line { Content = block.Substring(start, i - start), Break = block.Substring(i, breakLength) });
                i += breakLength - 1;
                start = i + 1;
            }
            lines.Add(new Line { Content = block.Substring(start), Break = "" });
            return lines;
        }

        private static int LineStart(string text, int position)
        {
            var i = position;
            while (i > 0 && text[i - 1] != '\n' && text[i - 1] != '\r')
                i--;
            return i;
        }

        private static int LineEnd(string text, int position)
        {
            var i = position;
            while (i < text.Length && text[i] != '\n' && text[i] != '\r')
                i++;
            return i;
        }

        private static bool IsLineStart(string text, int position)
        {
            return position > 0 && position <= text.Length && (text[position - 1] == '\n' || text[position - 1] == '\r');
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
