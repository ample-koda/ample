using System.Security.Cryptography;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

/// <summary>Takes of WAV audio are kept compressed on disk and read back as exactly the same bytes.</summary>
[Trait("Category", "Takebank")]
public sealed class TakeCodecTests
{
    private static byte[] Wav(int samples, bool silent)
    {
        short[] data = new short[samples];
        if (!silent)
        {
            for (int i = 0; i < samples; i++)
            {
                data[i] = (short)(8000 * Math.Sin(2 * Math.PI * 220 * i / 44100.0));
            }
        }

        return Ample.Audio.Engine.Wav.Encode(data, 44100);
    }

    [Fact]
    public async Task A_wav_take_is_stored_smaller_and_read_back_byte_for_byte_with_the_same_address()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            byte[] silence = Wav(400_000, true);
            byte[] tone = Wav(200_000, false);
            string silenceId = await bank.PutDataAsync(new MemoryStream(silence));
            string toneId = await bank.PutDataAsync(new MemoryStream(tone));

            Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(silence)), silenceId);
            Assert.True(bank.DiskSizeOfData(silenceId) < silence.Length / 50);
            Assert.True(bank.DiskSizeOfData(toneId) < tone.Length);
            foreach ((string id, byte[] plain) in new[] { (silenceId, silence), (toneId, tone) })
            {
                using Stream read = bank.OpenData(id);
                using MemoryStream copy = new();
                read.CopyTo(copy);
                Assert.Equal(plain, copy.ToArray());
            }

            // small and non-WAV blobs stay plain
            string text = await bank.PutDataAsync(TakeFixtures.Bytes("not audio"));
            Assert.Equal(9, bank.DiskSizeOfData(text));

            // the same bytes stored again are one blob
            Assert.Equal(silenceId, await bank.PutDataAsync(new MemoryStream(silence)));

            // verification decodes and rehashes
            TakeRecord take = await (await bank.BeginAsync(TakeFixtures.Plan())).CommitAsync([("audio", new MemoryStream(tone))]);
            Assert.Empty(await bank.VerifyAsync(take.TakeId));
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task The_setting_wav_keeps_new_data_plain()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            new StorageSettings(10, 0, 60, null, null, "wav", true).Save(root);
            byte[] silence = Wav(400_000, true);
            string id = await bank.PutDataAsync(new MemoryStream(silence));
            Assert.Equal(silence.Length, bank.DiskSizeOfData(id));
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Plain_takes_can_be_compressed_in_place_without_changing_their_bytes()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            new StorageSettings(10, 0, 60, null, null, "wav", true).Save(root);
            byte[] plain = Wav(400_000, true);
            string id = await bank.PutDataAsync(new MemoryStream(plain));
            Assert.True(bank.IsPlainWav(id));
            long freed = bank.CompressInPlace(id);
            Assert.True(freed > plain.Length / 2);
            Assert.False(bank.IsPlainWav(id));
            using Stream read = bank.OpenData(id);
            using MemoryStream copy = new();
            read.CopyTo(copy);
            Assert.Equal(plain, copy.ToArray());
            Assert.Equal(0, bank.CompressInPlace(id));   // already compressed
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }
}
