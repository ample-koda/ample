using System.Security.Cryptography;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

/// <summary>A buffer that is in memory is stored with one write and the same content address as the stream path (compressed storage included).</summary>
[Trait("Category", "Takebank")]
public sealed class TakeBufferTests
{
    private static byte[] Wav(int samples)
    {
        short[] data = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            data[i] = (short)(8000 * Math.Sin(2 * Math.PI * 220 * i / 44100.0));
        }

        return Ample.Audio.Engine.Wav.Encode(data, 44100);
    }

    [Fact]
    public async Task A_buffer_gets_the_same_address_the_same_size_on_disk_and_the_same_bytes_back_as_the_stream_path()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            byte[] tone = Wav(200_000);

            string viaBuffer = await bank.PutBufferAsync(tone);
            long sizeViaBuffer = bank.DiskSizeOfData(viaBuffer);
            string viaStream = await bank.PutDataAsync(new MemoryStream(tone));

            Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(tone)), viaBuffer);
            Assert.Equal(viaStream, viaBuffer);   // the same blob: nothing was written twice
            Assert.True(sizeViaBuffer < tone.Length, "packed under compressed storage");
            using Stream read = bank.OpenData(viaBuffer);
            using MemoryStream copy = new();
            read.CopyTo(copy);
            Assert.Equal(tone, copy.ToArray());
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_buffer_that_is_stored_already_is_not_written_again_and_leaves_no_temp_file()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            byte[] tone = Wav(200_000);
            string first = await bank.PutBufferAsync(tone);
            long size = bank.DiskSizeOfData(first);

            string second = await bank.PutBufferAsync(tone);

            Assert.Equal(first, second);
            Assert.Equal(size, bank.DiskSizeOfData(second));
            string temp = Path.Combine(root, ".ample", "tmp");
            Assert.True(!Directory.Exists(temp) || Directory.GetFiles(temp).Length == 0, "no half written file is left");
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Small_and_non_wav_buffers_and_the_wav_setting_stay_plain()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            byte[] text = System.Text.Encoding.UTF8.GetBytes("not audio");
            string textId = await bank.PutBufferAsync(text);
            Assert.Equal(text.Length, bank.DiskSizeOfData(textId));

            new StorageSettings(10, 0, 60, null, null, "wav", true).Save(root);
            byte[] tone = Wav(200_000);
            string plainId = await bank.PutBufferAsync(tone);
            Assert.Equal(tone.Length, bank.DiskSizeOfData(plainId));
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_committed_take_of_a_memory_buffer_verifies()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            byte[] tone = Wav(200_000);
            TakeRecord take = await (await bank.BeginAsync(TakeFixtures.Plan())).CommitAsync([("audio", new MemoryStream(tone))]);

            Assert.Empty(await bank.VerifyAsync(take.TakeId));
            using Stream read = bank.OpenData(take.Data[0].DataId);
            using MemoryStream copy = new();
            read.CopyTo(copy);
            Assert.Equal(tone, copy.ToArray());
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }
}
