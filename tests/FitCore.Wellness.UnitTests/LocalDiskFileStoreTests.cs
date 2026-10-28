using FitCore.Infrastructure.Core;

namespace FitCore.Wellness.UnitTests;

public sealed class LocalDiskFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fitcore-store-" + Guid.NewGuid().ToString("N"));
    private readonly LocalDiskFileStore _store;

    public LocalDiskFileStoreTests() => _store = new LocalDiskFileStore(Path.Combine(_root, "files"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static MemoryStream Bytes(params byte[] data) => new(data);

    [Fact]
    public async Task Guarda_lee_y_borra_un_archivo()
    {
        await _store.SaveAsync("progress-photos/abc/def", Bytes(1, 2, 3), default);

        await using (var read = await _store.OpenReadAsync("progress-photos/abc/def", default))
        {
            Assert.NotNull(read);
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy);
            Assert.Equal([1, 2, 3], copy.ToArray());
        }

        await _store.DeleteAsync("progress-photos/abc/def", default);
        Assert.Null(await _store.OpenReadAsync("progress-photos/abc/def", default));
    }

    [Fact]
    public async Task Borrar_es_idempotente_y_leer_algo_inexistente_devuelve_nulo()
    {
        await _store.DeleteAsync("progress-photos/no/existe", default);
        await _store.DeleteAsync("progress-photos/no/existe", default);

        Assert.Null(await _store.OpenReadAsync("progress-photos/no/existe", default));
    }

    [Fact]
    public async Task Una_clave_no_se_reutiliza_y_no_quedan_archivos_temporales()
    {
        await _store.SaveAsync("a/b", Bytes(1), default);

        await Assert.ThrowsAnyAsync<IOException>(() => _store.SaveAsync("a/b", Bytes(2), default));

        var files = Directory.GetFiles(Path.Combine(_root, "files"), "*", SearchOption.AllDirectories);
        Assert.Equal(["b"], files.Select(Path.GetFileName));
        await using var read = await _store.OpenReadAsync("a/b", default);
        Assert.Equal(1, read!.ReadByte());
    }

    [Theory]
    [InlineData("../fuera")]
    [InlineData("a/../../fuera")]
    [InlineData("a/../b")]
    [InlineData("/absoluta")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("A/b")]
    [InlineData("a/b.jpg")]
    [InlineData("a\\b")]
    [InlineData("C:/windows")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task Las_claves_fuera_del_formato_permitido_se_rechazan(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(key, Bytes(1), default));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.OpenReadAsync(key, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync(key, default));
        Assert.False(File.Exists(Path.Combine(_root, "fuera")));
    }
}
