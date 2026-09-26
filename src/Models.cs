using FluentValidation;

namespace TestJob.Api;

public sealed record ProcessingRequest
{
    public string? Selector { get; init; }
    public string? Attribute { get; init; }
    public string? UrlB64 { get; init; }
    public string? EncryptedTextBytesB64 { get; init; }
    public string? KeyBytesB64 { get; init; }
    public string? PageB64 { get; init; }
}

public sealed record ProcessingResponse
{
    public int IsError { get; init; }
    public string ErrorCode { get; init; } = "";
    public string ErrorMessage { get; init; } = "";
    public int ElementsCount { get; init; }
    public int EmailsCount { get; init; }
    public string Url { get; init; } = "";
    public string DecryptedPlainText { get; init; } = "";
    public List<string> ElementsAttrList { get; init; } = [];
    public List<string> EmailsList { get; init; } = [];

    public static ProcessingResponse Error(string code, string message) =>
        new() { IsError = 1, ErrorCode = code, ErrorMessage = message };
}

public sealed class ProcessingRequestValidator : AbstractValidator<ProcessingRequest>
{
    public ProcessingRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Selector)
            .NotNull().WithErrorCode("MISSING_PARAMETER").WithMessage("selector is required.")
            .NotEmpty().WithErrorCode("EMPTY_SELECTOR").WithMessage("selector must not be empty.");
        RuleFor(x => x.Attribute)
            .NotNull().WithErrorCode("MISSING_PARAMETER").WithMessage("attribute is required.")
            .NotEmpty().WithErrorCode("EMPTY_ATTRIBUTE").WithMessage("attribute must not be empty.")
            .Must(value => value!.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c)
                && !"\"'<>/=".Contains(c)))
            .WithErrorCode("INVALID_ATTRIBUTE").WithMessage("attribute must be an HTML attribute name.");
        RuleFor(x => x.UrlB64)
            .NotEmpty().WithErrorCode("MISSING_PARAMETER").WithMessage("url_b64 is required.");
        RuleFor(x => x.PageB64)
            .NotEmpty().WithErrorCode("MISSING_PARAMETER").WithMessage("page_b64 is required.");
        RuleFor(x => x.KeyBytesB64)
            .NotEmpty().WithErrorCode("MISSING_PARAMETER").WithMessage("key_bytes_b64 is required.");
        RuleFor(x => x.EncryptedTextBytesB64)
            .NotEmpty().WithErrorCode("MISSING_PARAMETER").WithMessage("encrypted_text_bytes_b64 is required.");
    }
}

public sealed class RequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
