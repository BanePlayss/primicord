using System.Net;
using Primicord;
using Xunit;

namespace Primicord.Tests;

/// <summary>
/// A camera na malha: empacotamento, remontagem e — o que mais importa — tela e
/// camera da MESMA pessoa nao se atrapalhando.
/// </summary>
[Collection("rede")]
public sealed class WebcamProtocolTests
{
    private static async Task<(RoomSession A, RoomSession B)> PairAsync()
    {
        var fs = new Firestore();
        var a = new RoomSession(fs, "sala-cam", "peer-aaa", "A");
        var b = new RoomSession(fs, "sala-cam", "peer-bbb", "B");
        await a.StartNetworkOnlyAsync(useStun: false);
        await b.StartNetworkOnlyAsync(useStun: false);
        a.AddPeerDirect("peer-bbb", "B", new[] { new IPEndPoint(IPAddress.Loopback, b.LocalPort) });
        b.AddPeerDirect("peer-aaa", "A", new[] { new IPEndPoint(IPAddress.Loopback, a.LocalPort) });
        Assert.True(await Wait.UntilAsync(
            () => a.Peers.Any(p => p.Connected) && b.Peers.Any(p => p.Connected), 10_000),
            "os dois lados deveriam ter conectado");
        return (a, b);
    }

    private static byte[] Padrao(int tamanho, int semente)
    {
        var buf = new byte[tamanho];
        new Random(semente).NextBytes(buf);
        return buf;
    }

    [Fact]
    public async Task Quadro_de_camera_remonta_identico()
    {
        var (a, b) = await PairAsync();
        using (a) using (b)
        {
            // Bem acima do payload de um datagrama (1100B): tem que fragmentar.
            var quadro = Padrao(7_400, 42);   // ~ um JPEG de 640x480 da C270

            byte[]? recebido = null;
            int gw = 0, gh = 0;
            b.WebcamFrameReceived += (_, payload, w, h) =>
            {
                if (recebido == null) { recebido = payload; gw = w; gh = h; }
            };

            a.SendWebcamFrame(quadro, quadro.Length, 640, 480);

            Assert.True(await Wait.UntilAsync(() => recebido != null, 5_000), "a camera nao chegou");
            Assert.Equal(640, gw);
            Assert.Equal(480, gh);
            Assert.Equal(quadro, recebido);
        }
    }

    [Fact]
    public async Task Camera_nao_chega_no_evento_de_tela()
    {
        var (a, b) = await PairAsync();
        using (a) using (b)
        {
            int naTela = 0, naCamera = 0;
            b.ScreenFrameReceived += (_, _, _, _) => Interlocked.Increment(ref naTela);
            b.WebcamFrameReceived += (_, _, _, _) => Interlocked.Increment(ref naCamera);

            a.SendWebcamFrame(Padrao(3_000, 7), 3_000, 320, 240);

            Assert.True(await Wait.UntilAsync(() => naCamera == 1, 5_000), "a camera nao chegou");
            await Task.Delay(300);
            Assert.Equal(0, naTela);
        }
    }

    [Fact]
    public async Task Tela_e_camera_ao_mesmo_tempo_nao_se_corrompem()
    {
        // ESTE e o teste que importa. A remontagem era indexada so por REMETENTE:
        // a mesma pessoa mandando tela e camera junto tinha os dois fluxos brigando
        // pelo mesmo estado, e cada quadro de um jogava fora o quadro pela metade do
        // outro. Agora o tipo faz parte da chave.
        //
        // OS DOIS PRECISAM SAIR DE THREADS DIFERENTES. Mandando em sequencia, os
        // fragmentos da tela chegam todos antes do primeiro da camera em loopback e
        // nao ha intercalacao nenhuma — o teste passa ate com o bug. Na vida real
        // sao o ScreenSender e a WebcamCapture, cada um na sua thread.
        var (a, b) = await PairAsync();
        using (a) using (b)
        {
            // Contagens de fragmentos DIFERENTES (9 e 7), que e o que dispara o
            // reset do estado compartilhado quando a chave nao separa os fluxos.
            var tela = Padrao(9_500, 1);
            var camera = Padrao(7_400, 2);
            const int quadros = 25;

            int telasOk = 0, camerasOk = 0, telasRuins = 0, camerasRuins = 0;
            b.ScreenFrameReceived += (_, p, _, _) =>
            {
                if (p.AsSpan().SequenceEqual(tela)) Interlocked.Increment(ref telasOk);
                else Interlocked.Increment(ref telasRuins);
            };
            b.WebcamFrameReceived += (_, p, _, _) =>
            {
                if (p.AsSpan().SequenceEqual(camera)) Interlocked.Increment(ref camerasOk);
                else Interlocked.Increment(ref camerasRuins);
            };

            // SEM pausa entre quadros: cada rajada dura microssegundos, entao com
            // intervalo elas quase nunca se encavalam e o bug nao aparece. Sem
            // intervalo, os fragmentos dos dois fluxos se misturam de verdade na
            // fila de recepcao — que e a condicao que quebrava a remontagem.
            var largada = new ManualResetEventSlim(false);
            await Task.WhenAll(
                Task.Run(() => { largada.Wait(); for (int i = 0; i < quadros; i++) a.SendScreenFrame(tela, tela.Length, 1920, 1080); }),
                Task.Run(() => { largada.Wait(); for (int i = 0; i < quadros; i++) a.SendWebcamFrame(camera, camera.Length, 640, 480); }),
                Task.Run(() => { Thread.Sleep(50); largada.Set(); }));

            await Task.Delay(1000);

            // Loopback quase nao perde, entao a folga aqui e pra jitter de
            // agendamento, nao pra perda estrutural. Com o estado compartilhado,
            // um dos fluxos praticamente nao fecha quadro nenhum.
            int piso = quadros / 2;
            Assert.True(telasOk >= piso && camerasOk >= piso,
                $"tela {telasOk}/{quadros}, camera {camerasOk}/{quadros} (piso {piso})");
            Assert.Equal(0, telasRuins);
            Assert.Equal(0, camerasRuins);
        }
    }
}

/// <summary>A parede de cameras: decodificacao, validade e limpeza.</summary>
public sealed class WebcamWallTests
{
    private static byte[] JpegDeVerdade(int w, int h)
    {
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.CornflowerBlue);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
        return ms.ToArray();
    }

    [Fact]
    public void Quadro_valido_vira_imagem_no_tamanho_do_tile()
    {
        using var wall = new WebcamWall(186, 124);
        Assert.True(wall.OnFrame(1234, JpegDeVerdade(640, 480)));

        var img = wall.FrameOf(1234);
        Assert.NotNull(img);
        // Reduzido UMA vez aqui, nao a cada repintura da UI.
        Assert.Equal(186, img!.Width);
        Assert.Equal(124, img.Height);
    }

    [Fact]
    public void Quadro_corrompido_e_recusado_sem_estourar()
    {
        using var wall = new WebcamWall();
        // E o que um pedaco perdido produz: JPEG truncado.
        var lixo = JpegDeVerdade(320, 240)[..40];
        Assert.False(wall.OnFrame(9, lixo));
        Assert.Null(wall.FrameOf(9));
    }

    [Fact]
    public void Quem_nunca_mandou_nao_tem_imagem()
    {
        using var wall = new WebcamWall();
        Assert.Null(wall.FrameOf(777));
    }

    [Fact]
    public void Imagem_entregue_continua_valida_depois_de_quadros_novos()
    {
        // A INVARIANTE QUE O X VERMELHO QUEBROU. A previa local nao passava por
        // aqui: ela criava uma Image por quadro e DESCARTAVA a anterior. A UI, que
        // ja tinha a referencia, pintava um objeto morto — e excecao no OnPaint faz
        // o WinForms desenhar um X vermelho no lugar do controle.
        //
        // O sintoma visual nao da pra reproduzir sem message loop de verdade (o
        // DrawToBitmap engole a excecao). Entao o que se testa aqui e a causa: quem
        // pegou uma imagem pode continuar usando enquanto quadros novos chegam.
        using var wall = new WebcamWall();
        wall.OnFrame(1, JpegDeVerdade(320, 240));

        var img = wall.FrameOf(1);
        Assert.NotNull(img);

        for (int i = 0; i < 10; i++) wall.OnFrame(1, JpegDeVerdade(320, 240));

        // Se o bitmap tivesse sido trocado e descartado, ler daqui lancaria.
        _ = img!.Width;
        _ = img.Height;
        Assert.Same(img, wall.FrameOf(1));   // e o MESMO bitmap, repintado
    }

    [Fact]
    public void Remover_solta_a_imagem()
    {
        using var wall = new WebcamWall();
        wall.OnFrame(5, JpegDeVerdade(320, 240));
        Assert.NotNull(wall.FrameOf(5));
        wall.Remove(5);
        Assert.Null(wall.FrameOf(5));
    }
}
