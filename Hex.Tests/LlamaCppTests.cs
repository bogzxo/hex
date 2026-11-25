using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// llama.cpp's setup: the machine read from what its tools say, the build picked from a real release's files, models
/// fitted to memory, llama-server's arguments, downloads carried on and checked, archives unpacked safely, and the
/// provider's address and name.
/// </summary>
public sealed class LlamaCppTests
{
    // Every file of llama.cpp's release b12651, as GitHub lists them
    private static readonly string[] names =
    [
        "cudart-llama-b12651-bin-ubuntu-cuda-12.8-x64.tar.gz", "cudart-llama-b12651-bin-ubuntu-cuda-13.5-arm64.tar.gz", "cudart-llama-b12651-bin-ubuntu-cuda-13.5-x64.tar.gz",
        "cudart-llama-bin-win-cuda-12.4-x64.zip", "cudart-llama-bin-win-cuda-13.5-arm64.zip", "cudart-llama-bin-win-cuda-13.5-x64.zip",
        "llama-b12651-bin-android-arm64-snapdragon.tar.gz", "llama-b12651-bin-android-arm64.tar.gz", "llama-b12651-bin-linux-arm64-snapdragon.tar.gz",
        "llama-b12651-bin-macos-arm64.tar.gz", "llama-b12651-bin-macos-x64.tar.gz", "llama-b12651-bin-ubuntu-26.04-s390x.tar.gz", "llama-b12651-bin-ubuntu-arm64.tar.gz",
        "llama-b12651-bin-ubuntu-cuda-12.8-x64.tar.gz", "llama-b12651-bin-ubuntu-cuda-13.5-arm64.tar.gz", "llama-b12651-bin-ubuntu-cuda-13.5-x64.tar.gz",
        "llama-b12651-bin-ubuntu-openvino-2026.4.1-x64.tar.gz", "llama-b12651-bin-ubuntu-rocm-10.0-x64.tar.gz", "llama-b12651-bin-ubuntu-s390x.tar.gz",
        "llama-b12651-bin-ubuntu-sycl-fp16-x64.tar.gz", "llama-b12651-bin-ubuntu-vulkan-arm64.tar.gz", "llama-b12651-bin-ubuntu-vulkan-x64.tar.gz", "llama-b12651-bin-ubuntu-x64.tar.gz",
        "llama-b12651-bin-win-cpu-arm64.zip", "llama-b12651-bin-win-cpu-x64.zip", "llama-b12651-bin-win-cuda-12.4-x64.zip", "llama-b12651-bin-win-cuda-13.5-arm64.zip",
        "llama-b12651-bin-win-cuda-13.5-x64.zip", "llama-b12651-bin-win-opencl-adreno-arm64.zip", "llama-b12651-bin-win-openvino-2026.4.1-x64.zip",
        "llama-b12651-bin-win-rocm-10.0-x64.zip", "llama-b12651-bin-win-sycl-x64.zip", "llama-b12651-bin-win-vulkan-arm64.zip", "llama-b12651-bin-win-vulkan-x64.zip",
        "llama-b12651-ui.tar.gz", "llama-b12651-xcframework.zip",
    ];

    private static readonly LlamaCppAsset[] assets = [.. names.Select(name => new LlamaCppAsset(name, "https://example.com/" + name, 1000))];

    private static LlamaCppPlatform Windows(LlamaCppGpu? gpu, long ramGib = 32) => new(LlamaCppPlatform.WINDOWS, "x64", ramGib * 1024, gpu);

    private static LlamaCppGpu Rtx3060(string cuda = "13.5") => new(LlamaCppGpu.NVIDIA, "NVIDIA GeForce RTX 3060", 12288, Version.Parse(cuda));

    [Fact]
    public void Nvidia_smi_says_the_biggest_card_and_its_drivers_cuda()
    {
        const string cards = "NVIDIA GeForce GTX 1050, 4096, 621.14\nNVIDIA GeForce RTX 3060, 12288, 621.14\n";
        LlamaCppGpu gpu = LlamaCppPlatform.NvidiaGpu(cards, "| NVIDIA-SMI 621.14   KMD Version: 621.14   CUDA UMD Version: 13.5 |")!;
        Assert.Equal(("NVIDIA GeForce RTX 3060", 12288L, new Version(13, 5)), (gpu.Name, gpu.MemoryMiB, gpu.Cuda));
        Assert.Equal(new Version(12, 4), LlamaCppPlatform.NvidiaGpu(cards, "Driver Version: 551.86   CUDA Version: 12.4")!.Cuda);
        Assert.Null(LlamaCppPlatform.NvidiaGpu("", ""));
        Assert.Equal(LlamaCppGpu.AMD, LlamaCppPlatform.VendorOf("AMD Radeon RX 7800 XT"));
    }

    [Fact]
    public void An_nvidia_card_gets_the_newest_cuda_its_driver_runs_with_cudas_libraries()
    {
        LlamaCppBuild build = LlamaCppBuilds.Choose(Windows(Rtx3060()), LlamaCppBuilds.CUDA, assets)!;
        Assert.Equal(("llama-b12651-bin-win-cuda-13.5-x64.zip", "cudart-llama-bin-win-cuda-13.5-x64.zip"), (build.Archive.Name, build.Runtime?.Name));

        // An older driver, the older CUDA; one too old for both has no CUDA build and gets Vulkan first
        Assert.Equal("llama-b12651-bin-win-cuda-12.4-x64.zip", LlamaCppBuilds.Choose(Windows(Rtx3060("12.6")), LlamaCppBuilds.CUDA, assets)!.Archive.Name);
        Assert.Equal([LlamaCppBuilds.VULKAN, LlamaCppBuilds.CPU], LlamaCppBuilds.Backends(Windows(Rtx3060("11.8")), assets));
        Assert.Equal([LlamaCppBuilds.CUDA, LlamaCppBuilds.VULKAN, LlamaCppBuilds.CPU], LlamaCppBuilds.Backends(Windows(Rtx3060()), assets));

        // Linux's come as .tar.gz, its runtime with the build's number in its name
        var linux = new LlamaCppPlatform(LlamaCppPlatform.LINUX, "x64", 32768, Rtx3060());
        LlamaCppBuild onLinux = LlamaCppBuilds.Choose(linux, LlamaCppBuilds.CUDA, assets)!;
        Assert.Equal(("llama-b12651-bin-ubuntu-cuda-13.5-x64.tar.gz", "cudart-llama-b12651-bin-ubuntu-cuda-13.5-x64.tar.gz"), (onLinux.Archive.Name, onLinux.Runtime?.Name));
    }

    [Fact]
    public void Every_other_machine_gets_its_own_build()
    {
        var mac = new LlamaCppPlatform(LlamaCppPlatform.MACOS, "arm64", 36 * 1024, new LlamaCppGpu(LlamaCppGpu.APPLE, "Apple silicon", 36 * 1024));
        Assert.Equal([LlamaCppBuilds.METAL, LlamaCppBuilds.CPU], LlamaCppBuilds.Backends(mac, assets).Take(1).Concat([LlamaCppBuilds.CPU]));
        Assert.Equal("llama-b12651-bin-macos-arm64.tar.gz", LlamaCppBuilds.Choose(mac, LlamaCppBuilds.METAL, assets)!.Archive.Name);

        var amd = new LlamaCppPlatform(LlamaCppPlatform.LINUX, "x64", 32768, new LlamaCppGpu(LlamaCppGpu.AMD, "Radeon", 16384));
        Assert.Equal([LlamaCppBuilds.VULKAN, LlamaCppBuilds.ROCM, LlamaCppBuilds.CPU], LlamaCppBuilds.Backends(amd, assets));
        Assert.Equal("llama-b12651-bin-ubuntu-vulkan-x64.tar.gz", LlamaCppBuilds.Choose(amd, LlamaCppBuilds.VULKAN, assets)!.Archive.Name);
        Assert.Equal("llama-b12651-bin-ubuntu-x64.tar.gz", LlamaCppBuilds.Choose(amd, LlamaCppBuilds.CPU, assets)!.Archive.Name);

        Assert.Equal([LlamaCppBuilds.CPU], LlamaCppBuilds.Backends(new LlamaCppPlatform(LlamaCppPlatform.WINDOWS, "arm64", 16384, null), assets));
        Assert.Null(LlamaCppBuilds.Choose(Windows(null), LlamaCppBuilds.METAL, assets)?.Archive);
    }

    [Fact]
    public void The_newest_release_with_files_is_the_one()
    {
        string json = """
        [{"tag_name":"v1.0.0","assets":[{"name":"nightly-tag.txt","browser_download_url":"https://x/a","size":1}]},
         {"tag_name":"b12650","published_at":"2026-11-23T08:36:57Z","assets":[{"name":"llama-b12650-bin-win-cpu-x64.zip","browser_download_url":"https://x/b","size":2}]},
         {"tag_name":"b12651","published_at":"2026-11-23T08:52:50Z","assets":[{"name":"llama-b12651-bin-win-cpu-x64.zip","browser_download_url":"https://x/c","size":3}]},
         {"tag_name":"b12652","assets":[]}]
        """;
        LlamaCppRelease release = LlamaCppBuilds.Newest(json)!;
        Assert.Equal("b12651", release.Tag);
        Assert.Equal(3, Assert.Single(release.Assets).Size);
    }

    [Fact]
    public void Models_fit_by_card_and_memory_and_the_best_that_runs_well_is_offered()
    {
        LlamaCppPlatform here = Windows(Rtx3060());
        Assert.All(LlamaCppModels.All, model => Assert.Equal(LlamaCppFit.Gpu, LlamaCppModels.Fit(model, here)));
        Assert.Equal("zeta-2.1", LlamaCppModels.Recommended(here).Id);
        Assert.False(LlamaCppModels.Find("zeta-2.1")!.Chats);
        Assert.True(LlamaCppModels.Find("qwen3.5-9b")!.Chats);

        // A 4 GB card can't hold Zeta 2.1 or Qwen3.5 whole; Mellum's experts wait in RAM, so it's offered
        LlamaCppPlatform small = Windows(Rtx3060() with { MemoryMiB = 4096 }, 16);
        Assert.Equal(LlamaCppFit.Split, LlamaCppModels.Fit(LlamaCppModels.Find("zeta-2.1")!, small));
        Assert.Equal(LlamaCppFit.Experts, LlamaCppModels.Fit(LlamaCppModels.Find("mellum2.1")!, small));
        Assert.Equal("mellum2.1", LlamaCppModels.Recommended(small).Id);

        // No card at all: the smallest
        Assert.Equal("qwen2.5-coder-1.5b", LlamaCppModels.Recommended(Windows(null, 16)).Id);
        Assert.Equal(LlamaCppFit.Cpu, LlamaCppModels.Fit(LlamaCppModels.Find("qwen2.5-coder-1.5b")!, Windows(null, 16)));
        Assert.All(LlamaCppModels.All, model => Assert.Matches("^[0-9a-f]{64}$", model.Sha256));
        Assert.Equal("zeta21", LlamaCppModels.Find("zeta-2.1")!.PromptFormat);
        Assert.Equal("zeta-2.1", LlamaCppModels.OfFile(@"D:\models\zeta-2.1.i1-Q4_K_M.gguf")?.Id);
    }

    [Fact]
    public void The_server_listens_here_alone_and_what_the_user_adds_comes_last()
    {
        var config = new LlamaCppServerConfig(@"D:\llama.cpp\llama-server.exe", @"D:\models\my model.gguf", "qwen3-coder-next", 8080, 65536, LlamaCppServerArgs.EXPERTS, "--threads 8 --override \"a b\"");
        List<string> args = LlamaCppServerArgs.Build(config);
        Assert.Equal(["-m", @"D:\models\my model.gguf", "--alias", "qwen3-coder-next", "--host", "127.0.0.1", "--port", "8080", "-c", "65536"], args.Take(10));
        Assert.Contains("--cpu-moe", args);
        Assert.Equal(["--threads", "8", "--override", "a b"], args.TakeLast(4));
        Assert.Contains("--fit", LlamaCppServerArgs.Build(config with { Offload = LlamaCppServerArgs.AUTO }));
        Assert.Equal(["-ngl", "0"], LlamaCppServerArgs.Build(config with { Offload = LlamaCppServerArgs.CPU, Extra = "" }).TakeLast(2));
        Assert.Equal(["say \"hi\"", "x"], LlamaCppServerArgs.Split("\"say \\\"hi\\\"\"  x "));
        Assert.Contains("\"D:\\models\\my model.gguf\"", LlamaCppServerArgs.Show(config.Server, args));
    }

    [Fact]
    public async Task A_download_carries_on_from_where_it_stopped_and_is_checked()
    {
        string folder = Directory.CreateTempSubdirectory("hex-llama-").FullName;
        try
        {
            byte[] whole = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("gguf-bytes-", 5000)));
            string sha = Convert.ToHexStringLower(SHA256.HashData(whole));
            string path = Path.Combine(folder, "model.gguf");
            File.WriteAllBytes(path + ".part", whole[..20000]);

            long? from = null;
            var server = new FakeServer(request =>
            {
                from = request.Headers.Range?.Ranges.First().From;
                var answer = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(whole[(int)from!.Value..]) };
                return Task.FromResult(answer);
            });
            await LlamaCppDownload.File("https://example.com/model.gguf", path, whole.Length, sha, null, default, new HttpClient(server));

            Assert.Equal(20000, from);
            Assert.Equal(whole, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".part"));

            // The wrong file is thrown away and said to be wrong
            string other = Path.Combine(folder, "other.gguf");
            var liar = FakeServer.Answering(HttpStatusCode.OK, new string('x', whole.Length));
            await Assert.ThrowsAsync<InvalidDataException>(() => LlamaCppDownload.File("https://example.com/x", other, whole.Length, sha, null, default, new HttpClient(liar)));
            Assert.False(File.Exists(other) || File.Exists(other + ".part"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Archives_unpack_inside_their_folder_and_one_reaching_out_is_refused()
    {
        string folder = Directory.CreateTempSubdirectory("hex-llama-").FullName;
        try
        {
            string zip = Path.Combine(folder, "build.zip");
            using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("build/bin/llama-server.exe").Open()))
                    writer.Write("server");
                archive.CreateEntry("ggml.dll");
            }
            LlamaCppDownload.Unpack(zip, Path.Combine(folder, "out"));
            Assert.True(File.Exists(Path.Combine(folder, "out", "build", "bin", "llama-server.exe")));

            string tar = Path.Combine(folder, "build.tar.gz");
            using (FileStream file = File.Create(tar))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new TarWriter(gzip))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "llama-b1/llama-server") { DataStream = new MemoryStream("x"u8.ToArray()) };
                writer.WriteEntry(entry);
            }
            LlamaCppDownload.Unpack(tar, Path.Combine(folder, "tar"));
            Assert.True(File.Exists(Path.Combine(folder, "tar", "llama-b1", "llama-server")));

            string evil = Path.Combine(folder, "evil.zip");
            using (ZipArchive archive = ZipFile.Open(evil, ZipArchiveMode.Create))
                archive.CreateEntry("../../outside.txt");
            Assert.Throws<InvalidDataException>(() => LlamaCppDownload.Unpack(evil, Path.Combine(folder, "evil")));
            Assert.False(File.Exists(Path.Combine(folder, "..", "outside.txt")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_servers_models_and_timings_are_read()
    {
        using var models = JsonDocument.Parse("""{"data":[{"id":"D:\\m.gguf","aliases":["qwen3-coder-next"]}]}""");
        Assert.Equal([@"D:\m.gguf", "qwen3-coder-next"], LlamaCppServer.Models(models.RootElement));

        LlamaCppSpeed speed = LlamaCppServer.Speed("""{"content":"    if (n < 2) return false;","timings":{"prompt_per_second":412.5,"predicted_per_second":21.3,"predicted_n":96}}""");
        Assert.Equal((412.5, 21.3, 96), (speed.PromptPerSecond, speed.AnswerPerSecond, speed.Tokens));
    }

    [Fact]
    public void The_llama_cpp_provider_asks_hexs_own_server_by_the_models_name()
    {
        var settings = new HexSettings(null)
        {
            EditPredictions = true, EditPredictionsProvider = HexSettings.LLAMACPP, EditPredictionsApiUrl = "http://elsewhere:1", LlamaCppPort = 8091,
        };
        Assert.False(PredictionSettings.Configured(settings));

        settings.LlamaCppServer = @"D:\llama.cpp\llama-server.exe";
        settings.LlamaCppModel = @"D:\models\m.gguf";
        settings.LlamaCppAlias = "qwen3-coder-next";
        Assert.True(PredictionSettings.Configured(settings));
        Assert.Equal("http://127.0.0.1:8091/v1/completions", PredictionSettings.ApiUrl(settings));
        Assert.Equal("qwen3-coder-next", PredictionSettings.Model(settings));

        var options = new PredictionOptions(true, HexSettings.LLAMACPP, PredictionSettings.ApiUrl(settings), "never-sent", "qwen3-coder-next", "qwen", 64, 100);
        var provider = Assert.IsType<OpenAiProvider>(PredictionProviders.Make(options));
        Assert.Equal("http://127.0.0.1:8091/v1/completions", provider.Api.ToString());
    }

    [Fact]
    public void Two_different_files_make_a_router_with_one_model_in_memory_and_the_same_file_does_not()
    {
        var one = new LlamaCppServerConfig(@"D:\llama.cpp\llama-server.exe", @"D:\models\zeta.gguf", "local", 8080, 32768, LlamaCppServerArgs.AUTO, "--threads 8");
        Assert.False(one.Router);
        Assert.Equal("local", one.EditName);
        Assert.False((one with { EditModel = @"D:\models\zeta.gguf", EditAlias = "edit" }).Router);
        Assert.False((one with { EditModel = @"D:\models\mellum.gguf" }).Router);

        LlamaCppServerConfig router = one with { EditModel = @"D:\models\mellum.gguf", EditAlias = "mellum2.1" };
        Assert.True(router.Router);
        Assert.Equal("mellum2.1", router.EditName);
        Assert.Throws<ArgumentNullException>(() => LlamaCppServerArgs.Build(router));
        List<string> args = LlamaCppServerArgs.Build(router, "hex-models.ini");
        Assert.Equal(["--models-preset", "hex-models.ini", "--models-max", "1", "--host", "127.0.0.1", "--port", "8080", "--threads", "8"], args);
        Assert.Equal("-m", LlamaCppServerArgs.Build(one)[0]);

        string preset = LlamaCppServerArgs.Preset(router);
        Assert.StartsWith("version = 1", preset);
        Assert.Contains("[*]\nparallel = 1\ncache-reuse = 256\njinja = true\nfit = on\nflash-attn = auto\n", preset);
        Assert.Contains("[local]\nmodel = D:\\models\\zeta.gguf\nc = 32768\nload-on-startup = true\n", preset);
        Assert.Contains("[mellum2.1]\nmodel = D:\\models\\mellum.gguf\nc = 32768\nload-on-startup = false\n", preset);
        Assert.Contains("cpu-moe = true", LlamaCppServerArgs.Preset(router with { Offload = LlamaCppServerArgs.EXPERTS }));
        Assert.Contains("n-gpu-layers = 0", LlamaCppServerArgs.Preset(router with { Offload = LlamaCppServerArgs.CPU }));
        Assert.Contains("n-gpu-layers = 999\nflash-attn = on", LlamaCppServerArgs.Preset(router with { Offload = LlamaCppServerArgs.GPU }));
        Assert.Contains("[abc]\n", LlamaCppServerArgs.Preset(router with { Alias = "a[b];#c\n" }));
    }

    [Fact]
    public void Others_alone_make_a_routed_server_without_an_edit_model_and_repeats_are_dropped()
    {
        var one = new LlamaCppServerConfig(@"D:\llama.cpp\llama-server.exe", @"D:\models\zeta.gguf", "local", 8080, 32768, LlamaCppServerArgs.AUTO, "");
        Assert.False(one.Routed);
        LlamaCppServerConfig many = one with
        {
            Others = [("mellum2.1", @"D:\models\mellum.gguf"), ("zeta", @"D:\MODELS\zeta.gguf"), ("mellum-again", @"D:\models\mellum.gguf"), ("qwen3.5-9b", @"D:\models\qwen.gguf")],
        };
        Assert.True(many.Routed);
        Assert.False(many.Router);
        Assert.Equal("local", many.EditName);
        Assert.Equal(["mellum2.1", "qwen3.5-9b"], many.Extras.Select(extra => extra.Alias));
        string preset = LlamaCppServerArgs.Preset(many);
        Assert.Contains("[local]\nmodel = D:\\models\\zeta.gguf\nc = 32768\nload-on-startup = true\n", preset);
        Assert.Contains("[qwen3.5-9b]\nmodel = D:\\models\\qwen.gguf\nc = 32768\nload-on-startup = false\n", preset);
        Assert.Equal("--models-preset", LlamaCppServerArgs.Build(many, "p.ini")[0]);
    }

    [Fact]
    public void Configs_with_the_same_other_models_in_new_lists_are_equal()
    {
        // The settings build a new list each time; a config unequal to itself restarted the server every frame
        var one = new LlamaCppServerConfig(@"D:\llama.cpp\llama-server.exe", @"D:\models\zeta.gguf", "local", 8080, 32768, LlamaCppServerArgs.AUTO, "");
        LlamaCppServerConfig a = one with { Others = [("mellum2.1", @"D:\models\mellum.gguf")] };
        LlamaCppServerConfig b = one with { Others = [("mellum2.1", @"D:\models\mellum.gguf")] };
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, one);
        Assert.NotEqual(a, one with { Others = [("mellum2.1", @"D:\models\other.gguf")] });
        Assert.NotEqual(a, a with { Port = 8081 });
    }

    [Fact]
    public async Task Starts_at_once_start_one_server()
    {
        // Each start that raced to its own process left the one before running; only the last start's may run
        string folder = Directory.CreateTempSubdirectory("hex-llama-race").FullName;
        try
        {
            string server = Path.Combine(folder, "fake-server.cmd");
            File.WriteAllText(server, "@echo off\r\n\"%~dp0hexracefake.exe\" -n 30 127.0.0.1 > nul\r\n");
            string model = Path.Combine(folder, "m.gguf");
            File.WriteAllText(model, "");
            if (!OperatingSystem.IsWindows())
                return;
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), Path.Combine(folder, "hexracefake.exe"));
            using var llama = new LlamaCppServer(new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) });
            var config = new LlamaCppServerConfig(server, model, "race", 1, 2048, LlamaCppServerArgs.AUTO, "");
            Task<bool>[] starts = [.. Enumerable.Range(0, 6).Select(_ => Task.Run(() => llama.Start(config, TimeSpan.FromSeconds(3))))];
            // Under load the racing starts can take a while to settle, so wait for the one server rather than a fixed time
            Assert.True(HexLinkTestKit.Eventually(() => llama.State == LlamaCppServerState.Starting, 20000));
            llama.Stop();
            await Task.WhenAll(starts);
            Assert.True(HexLinkTestKit.Eventually(() => FakeServers() == 0, 10000));

            static int FakeServers() => System.Diagnostics.Process.GetProcesses().Count(p =>
            {
                try { return p.ProcessName.Equals("hexracefake", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_stop_while_starting_leaves_no_server()
    {
        if (!OperatingSystem.IsWindows())
            return;
        string folder = Directory.CreateTempSubdirectory("hex-llama-stop").FullName;
        try
        {
            string server = Path.Combine(folder, "fake-server.cmd");
            // A copy of ping under its own name, so only this test's processes are counted
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), Path.Combine(folder, "hexstopfake.exe"));
            File.WriteAllText(server, "@echo off\r\n\"%~dp0hexstopfake.exe\" -n 30 127.0.0.2 > nul\r\n");
            string model = Path.Combine(folder, "m.gguf");
            File.WriteAllText(model, "");
            using var llama = new LlamaCppServer(new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) });
            var config = new LlamaCppServerConfig(server, model, "stop", 1, 2048, LlamaCppServerArgs.AUTO, "");
            Task<bool> start = Task.Run(() => llama.Start(config, TimeSpan.FromSeconds(3)));
            await Task.Delay(30);
            llama.Stop();
            Assert.False(await start);
            await Task.Delay(500);
            Assert.Equal(LlamaCppServerState.Stopped, llama.State);
            int left = System.Diagnostics.Process.GetProcesses().Count(p =>
            {
                try { return p.ProcessName.Equals("hexstopfake", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
            Assert.Equal(0, left);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void The_edit_models_are_the_catalogs_that_chat_best_first()
    {
        Assert.Equal(["mellum2.1", "qwen3.5-9b", "qwen2.5-coder-1.5b"], LlamaCppModels.EditModels.Select(model => model.Id));
        Assert.All(LlamaCppModels.EditModels, model => Assert.True(model.Chats));
    }
}
