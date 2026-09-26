using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Dapper;
using FluentValidation;
using Npgsql;

namespace TestJob.Api;

public sealed class ProcessingService(
    NpgsqlDataSource dataSource,
    IValidator<ProcessingRequest> validator)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string EmailAddress =
        @"[A-Z0-9_%+'-]+(?:\.[A-Z0-9_%+'-]+)*@" +
        @"(?:[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?\.)+[A-Z]{2,63}";
    private static readonly Regex EmailRegex = new(
        $$"""
        (?<![\p{L}\p{N}\p{M}._%+'@-])
        (?<quote>["'])(?<email>{{EmailAddress}})\k<quote>(?!@)
        |
        (?<![\p{L}\p{N}\p{M}._%+'@"-])
        (?<email>{{EmailAddress}})
        (?![\p{L}\p{N}\p{M}_@-]|\.[\p{L}\p{N}\p{M}_.-]|["']@)
        """,
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            | RegexOptions.IgnorePatternWhitespace,
        TimeSpan.FromSeconds(2));

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE IF NOT EXISTS elements (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                attribute_value text NOT NULL,
                html text NOT NULL
            );
            """, cancellationToken: cancellationToken));
    }

    public async Task<ProcessingResponse> ProcessAsync(
        ProcessingRequest request, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            throw new RequestException(validation.Errors[0].ErrorCode,
                string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));

        var url = DecodeUtf8(DecodeBase64(request.UrlB64!, "URL"), "URL");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new RequestException("INVALID_URL", "url_b64 must contain an absolute HTTP(S) URL.");

        var html = DecodeUtf8(DecodeBase64(request.PageB64!, "PAGE"), "PAGE");
        var key = DecodeBase64(request.KeyBytesB64!, "KEY");
        var encryptedText = DecodeBase64(request.EncryptedTextBytesB64!, "CIPHERTEXT");
        if (key.Length != 32)
            throw new RequestException("INVALID_KEY_SIZE", "AES-256 requires a 32-byte key.");
        if (encryptedText.Length == 0 || encryptedText.Length % 16 != 0)
            throw new RequestException("INVALID_CIPHERTEXT_SIZE", "Ciphertext must contain complete 16-byte AES blocks.");

        string plainText;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            // Preserve every plaintext byte: the contract explicitly specifies no padding.
            plainText = DecodeUtf8(aes.DecryptEcb(encryptedText, PaddingMode.None), "PLAINTEXT");
        }

        // HtmlParser does not load external CSS/scripts or execute JavaScript.
        using var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);
        IHtmlCollection<IElement> elements;
        try
        {
            elements = document.QuerySelectorAll(request.Selector!);
        }
        catch (DomException exception)
        {
            throw new RequestException("INVALID_SELECTOR", exception.Message);
        }

        var attributes = new List<string>(elements.Length);
        var elementHtml = new List<string>(elements.Length);
        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attributes.Add(element.GetAttribute(request.Attribute!) ?? "");
            elementHtml.Add(element.OuterHtml);
        }

        // Regex and in-memory AES have no true asynchronous API; Task.Run would only
        // consume an extra thread. Keep I/O asynchronous and these CPU operations local.
        var emails = new List<string>();
        foreach (Match match in EmailRegex.Matches(html))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Exclude enclosing HTML quotes, while preserving apostrophes in the address.
            emails.Add(match.Groups["email"].Value);
        }

        if (elements.Length > 0)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO elements (attribute_value, html)
                SELECT * FROM unnest(CAST(@Attributes AS text[]), CAST(@Html AS text[]));
                """, new { Attributes = attributes.ToArray(), Html = elementHtml.ToArray() },
                transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }

        return new ProcessingResponse
        {
            Url = url,
            DecryptedPlainText = plainText,
            ElementsCount = elements.Length,
            ElementsAttrList = attributes,
            EmailsCount = emails.Count,
            EmailsList = emails
        };
    }

    private static byte[] DecodeBase64(string value, string field)
    {
        try { return Convert.FromBase64String(value); }
        catch (FormatException)
        {
            throw new RequestException($"INVALID_{field}_BASE64", $"{field} contains invalid Base64.");
        }
    }

    private static string DecodeUtf8(byte[] value, string field)
    {
        try { return Utf8.GetString(value); }
        catch (DecoderFallbackException)
        {
            throw new RequestException($"INVALID_{field}_UTF8", $"{field} contains invalid UTF-8.");
        }
    }
}
