namespace DocLens.Lambda.Workers;

public enum FunctionRole
{
    Api,
    Processor,
    OcrResult
}

// All three Lambdas ship the same self-contained bootstrap binary
// (provided.al2023 ignores the handler string), so the process decides at
// startup which loop to run. DOCLENS_FUNCTION_ROLE is set explicitly by
// infra/terraform/modules/processing/lambdas.tf; the function-name match is
// only a fallback for functions deployed outside Terraform.
public static class FunctionRoleResolver
{
    public const string RoleVariable = "DOCLENS_FUNCTION_ROLE";

    public static FunctionRole Resolve(string? explicitRole, string? functionName)
    {
        switch (explicitRole?.Trim().ToLowerInvariant())
        {
            case "api":
                return FunctionRole.Api;
            case "processor":
                return FunctionRole.Processor;
            case "ocr_result":
            case "ocr-result":
                return FunctionRole.OcrResult;
        }

        if (functionName is not null)
        {
            if (functionName.StartsWith("doclens-processor-", StringComparison.Ordinal))
            {
                return FunctionRole.Processor;
            }

            if (functionName.StartsWith("doclens-ocr-result-", StringComparison.Ordinal))
            {
                return FunctionRole.OcrResult;
            }
        }

        return FunctionRole.Api;
    }
}
