using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Kuroko.RAG;

public class PdfParserService
{
    public string ExtractTextFromPdf(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        try
        {
            using var document = PdfDocument.Open(filePath);

            foreach (var page in document.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page);
                sb.AppendLine(text);
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }

        return sb.ToString();
    }

    public IEnumerable<string> ChunkText(string fullText, int maxTokensPerChunk = 300)
    {
        var paragraphs = fullText.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        var currentChunk = new StringBuilder();

        foreach (var para in paragraphs)
        {
            if (currentChunk.Length + para.Length > maxTokensPerChunk * 4)
            {
                yield return currentChunk.ToString().Trim();
                currentChunk.Clear();
            }

            currentChunk.AppendLine(para);
        }

        if (currentChunk.Length > 0)
        {
            yield return currentChunk.ToString().Trim();
        }
    }
}