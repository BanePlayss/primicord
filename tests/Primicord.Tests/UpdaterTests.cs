using Primicord;
using Xunit;

namespace Primicord.Tests;

/// <summary>
/// A comparacao de versoes decide se o app troca o proprio binario. Errar pra mais
/// faz o app se reinstalar em loop; errar pra menos faz o update nunca aparecer.
/// </summary>
public sealed class UpdaterTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("0.1.1", "0.1.0", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("0.0.9", "0.1.0", false)]
    public void Compara_versoes(string candidata, string atual, bool esperado)
    {
        Assert.Equal(esperado, Updater.IsNewer(candidata, atual));
    }

    [Fact]
    public void Dez_vem_depois_de_nove_e_nao_antes()
    {
        // A armadilha classica: comparado como TEXTO, "0.10.0" < "0.9.0" e o update
        // pararia de aparecer pra sempre depois da nona versao.
        Assert.True(Updater.IsNewer("0.10.0", "0.9.0"));
        Assert.False(Updater.IsNewer("0.9.0", "0.10.0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nao-e-versao")]
    [InlineData("v")]
    [InlineData("...")]
    public void Texto_invalido_nunca_dispara_atualizacao(string lixo)
    {
        // Uma tag mal digitada no GitHub nao pode fazer o app se substituir.
        Assert.False(Updater.IsNewer(lixo, "0.1.0"));
    }

    [Fact]
    public void A_versao_atual_e_legivel_e_conta_como_atualizada()
    {
        string atual = Updater.CurrentVersion;
        Assert.False(string.IsNullOrWhiteSpace(atual));
        Assert.False(Updater.IsNewer(atual, atual));
    }
}
