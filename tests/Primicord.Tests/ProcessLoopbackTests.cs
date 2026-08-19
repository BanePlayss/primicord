using Primicord;
using Xunit;

namespace Primicord.Tests;

public sealed class ProcessLoopbackTests
{
    /// <summary>
    /// Teste opt-in porque toca a pilha de audio real do Windows. E executado antes
    /// da release nesta maquina com PRIMICORD_TEST_AUDIO_LOOPBACK=1; no CI comum ele
    /// so garante que o caminho de interop continua compilando.
    /// </summary>
    [Fact]
    public void Ativa_process_loopback_real_sem_cast_com()
    {
        if (Environment.GetEnvironmentVariable("PRIMICORD_TEST_AUDIO_LOOPBACK") != "1") return;
        if (!ProcessLoopbackCapture.IsSupported) return;

        using var capture = ProcessLoopbackCapture.ExcludingSelf();
        capture.Prepare();
    }
}
