using DocLens.Lambda.Workers;

namespace DocLens.Lambda.Tests.Workers;

public class FunctionRoleResolverTests
{
    [Theory]
    [InlineData("api", null, FunctionRole.Api)]
    [InlineData("processor", "doclens-api-sbx", FunctionRole.Processor)]
    [InlineData("ocr_result", null, FunctionRole.OcrResult)]
    [InlineData(null, "doclens-processor-sbx", FunctionRole.Processor)]
    [InlineData(null, "doclens-ocr-result-prod", FunctionRole.OcrResult)]
    [InlineData(null, "doclens-api-sbx", FunctionRole.Api)]
    [InlineData(null, null, FunctionRole.Api)]
    public void Resolve_PrefersExplicitRole_ThenFunctionName(string? explicitRole, string? functionName, FunctionRole expected)
    {
        Assert.Equal(expected, FunctionRoleResolver.Resolve(explicitRole, functionName));
    }
}
