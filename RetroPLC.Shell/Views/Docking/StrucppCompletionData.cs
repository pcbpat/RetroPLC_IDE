// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using RetroPLC.LanguageServerHost;

namespace RetroPLC.Shell.Views.Docking;

internal sealed class StrucppCompletionData(StrucppCompletionItem item) : ICompletionData
{
    public IImage? Image => null;

    public string Text => item.Label;

    public object Content => item.Label;

    public object Description => string.IsNullOrWhiteSpace(item.Detail)
        ? item.Label
        : item.Detail;

    public double Priority =>
        double.TryParse(item.SortText, NumberStyles.Number, CultureInfo.InvariantCulture, out var sort)
            ? 100 - sort
            : 0;

    public void Complete(
        TextArea textArea,
        ISegment completionSegment,
        EventArgs insertionRequestEventArgs)
    {
        var insertionOffset = completionSegment.Offset;
        var source = item.InsertText ?? item.Label;
        var expansion = item.InsertTextFormat == 2
            ? ExpandSnippet(source)
            : new SnippetExpansion(source, source.Length, null);

        textArea.Document.Replace(completionSegment, expansion.Text);

        if (expansion.FirstPlaceholder is { } placeholder)
        {
            var start = insertionOffset + placeholder.Offset;
            textArea.Selection = Selection.Create(
                textArea,
                start,
                start + placeholder.Length);
            textArea.Caret.Offset = start + placeholder.Length;
        }
        else
        {
            textArea.Caret.Offset = insertionOffset + expansion.CaretOffset;
        }
    }

    private static SnippetExpansion ExpandSnippet(string snippet)
    {
        var output = new StringBuilder(snippet.Length);
        var placeholders = new List<SnippetPlaceholder>();
        int? finalCaret = null;

        for (var index = 0; index < snippet.Length;)
        {
            if (snippet[index] != '$')
            {
                output.Append(snippet[index]);
                index++;
                continue;
            }

            if (index + 1 < snippet.Length && snippet[index + 1] == '{')
            {
                var closingBrace = snippet.IndexOf('}', index + 2);
                if (closingBrace > index)
                {
                    var token = snippet[(index + 2)..closingBrace];
                    var colon = token.IndexOf(':');
                    var numberText = colon >= 0 ? token[..colon] : token;
                    if (int.TryParse(numberText, out var number))
                    {
                        var defaultText = colon >= 0 ? token[(colon + 1)..] : string.Empty;
                        if (number == 0)
                        {
                            finalCaret = output.Length;
                        }
                        else
                        {
                            placeholders.Add(
                                new SnippetPlaceholder(number, output.Length, defaultText.Length));
                        }
                        output.Append(defaultText);
                        index = closingBrace + 1;
                        continue;
                    }
                }
            }

            var digitEnd = index + 1;
            while (digitEnd < snippet.Length && char.IsDigit(snippet[digitEnd]))
                digitEnd++;
            if (digitEnd > index + 1 &&
                int.TryParse(snippet[(index + 1)..digitEnd], out var bareNumber))
            {
                if (bareNumber == 0)
                    finalCaret = output.Length;
                else
                    placeholders.Add(new SnippetPlaceholder(bareNumber, output.Length, 0));
                index = digitEnd;
                continue;
            }

            output.Append('$');
            index++;
        }

        var firstPlaceholder = placeholders
            .OrderBy(placeholder => placeholder.Number)
            .FirstOrDefault();
        return new SnippetExpansion(
            output.ToString(),
            finalCaret ?? output.Length,
            firstPlaceholder);
    }

    private sealed record SnippetExpansion(
        string Text,
        int CaretOffset,
        SnippetPlaceholder? FirstPlaceholder);

    private sealed record SnippetPlaceholder(int Number, int Offset, int Length);
}
