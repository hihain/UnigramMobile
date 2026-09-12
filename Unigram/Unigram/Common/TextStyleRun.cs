using System;
using System.Collections.Generic;
using System.Linq;
using Telegram.Td.Api;

namespace Unigram.Common
{
    public class TextStyleRun
    {
        public TextStyle Flags { get; set; }

        public int Start { get; set; }
        public int Offset => Start;

        public int End { get; set; }
        public int Length => End - Start;

        public TextEntityType Type { get; set; }

        public TextStyleRun()
        {

        }

        private TextStyleRun(TextStyleRun run)
        {
            Flags = run.Flags;
            Start = run.Start;
            End = run.End;
            Type = run.Type;
        }

        public bool HasFlag(TextStyle flag)
        {
            return Flags.HasFlag(flag);
        }

        private void Merge(TextStyleRun run)
        {
            Flags |= run.Flags;
            if (Type == null && run.Type != null)
            {
                Type = run.Type;
            }
        }

        public static IList<TextStyleRun> GetRuns(FormattedText formatted)
        {
            return GetRuns(formatted.Text, formatted.Entities);
        }

        public static IList<TextStyleRun> GetRuns(string text, IList<TextEntity> entities)
        {
            var runs = new List<TextStyleRun>();
            var entitiesCopy = new List<TextEntity>(entities);

            entitiesCopy.Sort((x, y) =>
            {
                if (x.Offset > y.Offset)
                {
                    return 1;
                }
                else if (y.Offset > x.Offset)
                {
                    return -1;
                }

                return 0;
            });

            for (int a = 0, N = entitiesCopy.Count; a < N; a++)
            {
                var entity = entitiesCopy[a];
                if (entity.Length <= 0 || entity.Offset < 0 || entity.Offset >= text.Length)
                {
                    continue;
                }
                else if (entity.Offset + entity.Length > text.Length)
                {
                    entity.Length = text.Length - entity.Offset;
                }

                var newRun = new TextStyleRun();
                newRun.Start = entity.Offset;
                newRun.End = newRun.Start + entity.Length;

                if (entity.Type is TextEntityTypeStrikethrough)
                {
                    newRun.Flags = TextStyle.Strikethrough;
                }
                else if (entity.Type is TextEntityTypeUnderline)
                {
                    newRun.Flags = TextStyle.Underline;
                }
                else if (entity.Type is TextEntityTypeSpoiler)
                {
                    newRun.Flags = TextStyle.Spoiler;
                    newRun.Type = entity.Type;
                }
                else if (entity.Type is TextEntityTypeBold)
                {
                    newRun.Flags = TextStyle.Bold;
                }
                else if (entity.Type is TextEntityTypeItalic)
                {
                    newRun.Flags = TextStyle.Italic;
                }
                else if (entity.Type is TextEntityTypeCode || entity.Type is TextEntityTypePre || entity.Type is TextEntityTypePreCode)
                {
                    newRun.Flags = TextStyle.Monospace;
                }
                else if (entity.Type is TextEntityTypeBlockQuote)
                {
                    // Without a flag of its own this lands in the final else below and is
                    // drawn as a link that leads nowhere, because Entity_Click has no branch
                    // for it. Callers that lay quotes out as blocks strip the entity before
                    // getting here; the ones that cannot -- chat list previews, the composer --
                    // are better off showing the text plainly.
                    newRun.Flags = TextStyle.Quote;
                }
                else if (entity.Type is TextEntityTypeMentionName)
                {
                    newRun.Flags = TextStyle.Mention;
                    newRun.Type = entity.Type;
                }
                else
                {
                    newRun.Flags = TextStyle.Url;
                    newRun.Type = entity.Type;
                }

                for (int b = 0, N2 = runs.Count; b < N2; b++)
                {
                    TextStyleRun run = runs[b];

                    if (newRun.Start > run.Start)
                    {
                        if (newRun.Start >= run.End)
                        {
                            continue;
                        }

                        if (newRun.End < run.End)
                        {
                            TextStyleRun r = new TextStyleRun(newRun);
                            r.Merge(run);
                            b++;
                            N2++;
                            runs.Insert(b, r);

                            r = new TextStyleRun(run);
                            r.Start = newRun.End;
                            b++;
                            N2++;
                            runs.Insert(b, r);
                        }
                        else if (newRun.End >= run.End)
                        {
                            TextStyleRun r = new TextStyleRun(newRun);
                            r.Merge(run);
                            r.End = run.End;
                            b++;
                            N2++;
                            runs.Insert(b, r);
                        }

                        int temp = newRun.Start;
                        newRun.Start = run.End;
                        run.End = temp;
                    }
                    else
                    {
                        if (run.Start >= newRun.End)
                        {
                            continue;
                        }
                        int temp = run.Start;
                        if (newRun.End == run.End)
                        {
                            run.Merge(newRun);
                        }
                        else if (newRun.End < run.End)
                        {
                            TextStyleRun r = new TextStyleRun(run);
                            r.Merge(newRun);
                            r.End = newRun.End;
                            b++;
                            N2++;
                            runs.Insert(b, r);

                            run.Start = newRun.End;
                        }
                        else
                        {
                            TextStyleRun r = new TextStyleRun(newRun);
                            r.Start = run.End;
                            b++;
                            N2++;
                            runs.Insert(b, r);

                            run.Merge(newRun);
                        }
                        newRun.End = temp;
                    }
                }
                if (newRun.Start < newRun.End)
                {
                    runs.Add(newRun);
                }
            }

            runs.Sort((x, y) =>
            {
                if (x.Offset > y.Offset)
                {
                    return 1;
                }
                else if (y.Offset > x.Offset)
                {
                    return -1;
                }

                return 0;
            });

            return runs;
        }

        public static IList<TextEntity> GetEntities(string text, IList<TextStyleRun> runs)
        {
            var results = new List<TextEntity>();

            foreach (var run in runs)
            {
                // Outside the monospace/else pair on purpose: a quote can contain a code
                // span, and folding it into either branch would lose one of the two.
                if (run.HasFlag(TextStyle.Quote))
                {
                    CreateOrMerge(run.Offset, run.Length, results, new TextEntityTypeBlockQuote());
                }

                if (run.HasFlag(TextStyle.Monospace))
                {
                    var part = text.Substring(run.Offset, run.Length);
                    if (part.Contains('\v') || part.Contains('\r'))
                    {
                        CreateOrMerge(run.Offset, run.Length, results, new TextEntityTypePre());
                    }
                    else
                    {
                        CreateOrMerge(run.Offset, run.Length, results, new TextEntityTypeCode());
                    }
                }
                else
                {
                    if (run.HasFlag(TextStyle.Bold))
                    {
                        CreateOrMerge(run.Offset, run.Length, results, new TextEntityTypeBold());
                    }
                    if (run.HasFlag(TextStyle.Italic))
                    {
                        CreateOrMerge(run.Offset, run.Length, results, new TextEntityTypeItalic());
                    }
                    if (run.HasFlag(TextStyle.Strikethrough))
                    {
                        CreateOrMerge(run.Offset, run.Length, results, new TextEntityTypeStrikethrough());
                    }
                    if (run.HasFlag(TextStyle.Underline))
                    {
                        CreateOrMerge(run.Offset, run.Length, results, new TextEntityTypeUnderline());
                    }
                    if (run.HasFlag(TextStyle.Spoiler))
                    {
                        CreateOrMerge(run.Offset, run.Length, results, new TextEntityTypeSpoiler());
                    }

                    if (run.Type != null)
                    {
                        CreateOrMerge(run.Offset, run.Length, results, run.Type);
                    }
                }
            }

            return results;
        }

        private static void CreateOrMerge(int offset, int length, IList<TextEntity> entities, TextEntityType type)
        {
            var last = entities.LastOrDefault(x => x.Length + x.Offset == offset && AreEqual(x.Type, type));
            if (last != null)
            {
                last.Length += length;
            }
            else
            {
                entities.Add(new TextEntity(offset, length, type));
            }
        }

        /// <summary>
        /// Whether two entity types are the same thing, which for the two that carry a
        /// destination means the same destination and not merely the same kind. Public
        /// because whoever builds the runs has to ask it as well: a run is a stretch of
        /// text with one entity on it, and two links that touch are two runs however alike
        /// their formatting looks.
        /// </summary>
        public static bool AreEqual(TextEntityType x, TextEntityType y)
        {
            if (x == null || y == null)
            {
                return x == y;
            }

            if (x is TextEntityTypeTextUrl xTextUrl && y is TextEntityTypeTextUrl yTextUrl)
            {
                return string.Equals(xTextUrl.Url, yTextUrl.Url, StringComparison.OrdinalIgnoreCase);
            }
            else if (x is TextEntityTypeMentionName xMentionName && y is TextEntityTypeMentionName yMentionName)
            {
                return xMentionName.UserId == yMentionName.UserId;
            }

            return x.GetType() == y.GetType();
        }
    }

    [Flags]
    public enum TextStyle
    {
        Bold = 1,
        Italic = 2,
        Monospace = 4,
        Strikethrough = 8,
        Underline = 16,
        Spoiler = 32,
        Mention = 64,
        Url = 128,
        Quote = 256
    }
}
