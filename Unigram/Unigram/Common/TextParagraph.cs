using System;
using System.Collections.Generic;
using Telegram.Td.Api;

namespace Unigram.Common
{
    public enum TextParagraphKind
    {
        Plain,
        Quote,
        Code
    }

    /// <summary>
    /// One stretch of a message that has to be laid out on its own.
    ///
    /// Every other kind of formatting Telegram sends is a character format and can be put
    /// on a Run inside the single Span a bubble already has. A quote and a code block
    /// cannot: both want a bar down the left of however many lines they happen to wrap to,
    /// and no inline can draw beside a line it is not on. So the text is cut into
    /// paragraphs first, and the ones that are blocks get their own control.
    /// </summary>
    public class TextParagraph
    {
        public TextParagraph(string text, IList<TextEntity> entities, TextParagraphKind kind, string language)
        {
            Text = text;
            Entities = entities;
            Kind = kind;
            Language = language;
        }

        public string Text { get; private set; }

        /// <summary>
        /// The entities that survived the cut, with their offsets rebased onto Text.
        /// </summary>
        public IList<TextEntity> Entities { get; private set; }

        public TextParagraphKind Kind { get; private set; }

        /// <summary>
        /// Set for a code block that named its language, null otherwise. Nothing renders it
        /// yet; it is carried through so that a syntax label does not need another pass.
        /// </summary>
        public string Language { get; private set; }
    }

    public static class TextParagraphs
    {
        /// <summary>
        /// The entity types that cannot be drawn inline. Note that TextEntityTypeCode --
        /// the single backtick kind -- is deliberately not one of them: it marks a word in
        /// the middle of a sentence and has to stay in the flow.
        /// </summary>
        public static bool IsBlock(TextEntityType type)
        {
            return type is TextEntityTypeBlockQuote
                || type is TextEntityTypePre
                || type is TextEntityTypePreCode;
        }

        public static bool HasBlocks(FormattedText text)
        {
            if (text == null || text.Entities == null)
            {
                return false;
            }

            foreach (var entity in text.Entities)
            {
                if (IsBlock(entity.Type))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Cuts text into paragraphs along its block entities. Text outside any block comes
        /// back as Plain, so the result is the whole message in order and nothing is lost.
        /// </summary>
        public static IList<TextParagraph> Split(string text, IList<TextEntity> entities)
        {
            var result = new List<TextParagraph>();

            if (string.IsNullOrEmpty(text))
            {
                return result;
            }

            if (entities == null)
            {
                entities = new TextEntity[0];
            }

            var blocks = new List<TextEntity>();

            foreach (var entity in entities)
            {
                if (!IsBlock(entity.Type))
                {
                    continue;
                }

                // The server is trusted about as far as the app can throw it: a block whose
                // range does not fit the text would take a Substring down with it.
                if (entity.Offset < 0 || entity.Length <= 0 || entity.Offset + entity.Length > text.Length)
                {
                    continue;
                }

                blocks.Add(entity);
            }

            blocks.Sort((x, y) => x.Offset.CompareTo(y.Offset));

            var previous = 0;

            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];

                // Blocks are not supposed to overlap or nest. If two ever do, the first one
                // wins rather than the text being emitted twice.
                if (block.Offset < previous)
                {
                    continue;
                }

                AddPlain(result, text, entities, previous, block.Offset, i > 0, true);

                var end = block.Offset + block.Length;
                result.Add(Create(text, entities, block.Offset, end, KindOf(block.Type), LanguageOf(block.Type)));

                previous = end;
            }

            AddPlain(result, text, entities, previous, text.Length, blocks.Count > 0, false);

            return result;
        }

        private static void AddPlain(IList<TextParagraph> result, string text, IList<TextEntity> entities, int start, int end, bool afterBlock, bool beforeBlock)
        {
            // A block is written on its own lines, so the text around it carries the
            // newline that separated them. Left in, that newline becomes a blank line above
            // or below the box, because the box is already a line of its own. Exactly one is
            // dropped at each seam -- a second one was meant as an empty line and stays.
            if (afterBlock && start < end && text[start] == '\r')
            {
                start++;
            }

            if (afterBlock && start < end && text[start] == '\n')
            {
                start++;
            }

            if (beforeBlock && end > start && text[end - 1] == '\n')
            {
                end--;

                if (end > start && text[end - 1] == '\r')
                {
                    end--;
                }
            }

            if (end > start)
            {
                result.Add(Create(text, entities, start, end, TextParagraphKind.Plain, null));
            }
        }

        private static TextParagraph Create(string text, IList<TextEntity> entities, int start, int end, TextParagraphKind kind, string language)
        {
            var sub = new List<TextEntity>();

            foreach (var entity in entities)
            {
                if (IsBlock(entity.Type))
                {
                    continue;
                }

                // Clipped rather than skipped: an entity that starts inside the paragraph and
                // runs past its end still has to format the part that is here.
                var from = Math.Max(entity.Offset, start);
                var to = Math.Min(entity.Offset + entity.Length, end);

                if (to > from)
                {
                    sub.Add(new TextEntity(from - start, to - from, entity.Type));
                }
            }

            return new TextParagraph(text.Substring(start, end - start), sub, kind, language);
        }

        private static TextParagraphKind KindOf(TextEntityType type)
        {
            return type is TextEntityTypeBlockQuote
                ? TextParagraphKind.Quote
                : TextParagraphKind.Code;
        }

        private static string LanguageOf(TextEntityType type)
        {
            var preCode = type as TextEntityTypePreCode;
            return preCode != null ? preCode.Language : null;
        }
    }
}
