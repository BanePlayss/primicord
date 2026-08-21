using Primicord;
using Xunit;

namespace Primicord.Tests;

public sealed class FirestoreTests
{
    [Theory]
    [InlineData("Firestore 429: Quota exceeded.")]
    [InlineData("Firestore 429: RESOURCE_EXHAUSTED")]
    public void Cota_esgotada_e_transitoria_e_identificavel(string message)
    {
        var ex = new FirestoreException(message);
        Assert.True(ex.IsQuotaExceeded);
        Assert.True(ex.IsTransient);
        Assert.False(ex.IsPermissionDenied);
    }

    [Fact]
    public void Permissao_negada_nao_e_confundida_com_cota()
    {
        var ex = new FirestoreException("Firestore 403: PERMISSION_DENIED");
        Assert.True(ex.IsPermissionDenied);
        Assert.False(ex.IsQuotaExceeded);
        Assert.False(ex.IsTransient);
    }
}
