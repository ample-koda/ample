namespace Ample.Audio.Engine;

/// <summary>
/// Granular time stretching and pitch shifting, integer only: overlapping triangular grains of 50 ms are laid down at a different distance than they were taken (speed), and a pitch
/// shift is such a stretch followed by a resample. It is a simple overlap-add: it keeps the length of the block and has the slightly grainy sound of that method.
/// </summary>
internal static class TimeStretch
{
    // 2^(k/12) in Q16 for k = 0 to 11
    private static readonly int[] SemitoneQ16 = [65536, 69433, 73562, 77936, 82570, 87480, 92682, 98193, 104032, 110218, 116772, 123715];

    /// <summary>The ratio 2^(semitones/12) in Q16 (a shift up is above 65536).</summary>
    internal static long RatioQ16(int semitones)
    {
        int octaves = Math.DivRem(semitones, 12, out int rest);
        if (rest < 0)
        {
            rest += 12;
            octaves--;
        }

        long ratio = SemitoneQ16[rest];
        return octaves >= 0 ? ratio << octaves : ratio >> -octaves;
    }

    /// <summary>The ratio 2^(hundredths/1200) in Q16 for a shift in hundredths of a semitone (cents): between two whole semitones the ratio goes in a straight line, which is off by less than a cent.</summary>
    internal static long RatioQ16Cents(int cents)
    {
        int whole = Math.DivRem(cents, 100, out int rest);
        if (rest < 0)
        {
            rest += 100;
            whole--;
        }

        long low = RatioQ16(whole), high = RatioQ16(whole + 1);
        return low + ((high - low) * rest / 100);
    }

    /// <summary>Plays the sound faster (above 100 percent) or slower (below), cut or padded with silence to the same length.</summary>
    internal static int[] Speed(int[] x, int percent, int rate)
    {
        int clamped = Math.Clamp(percent, 25, 400);
        return clamped == 100 ? x : Stretch(x, x.Length, clamped * 65536L / 100, rate);
    }

    /// <summary>Changes playback speed along the output timeline while keeping its length; each overlapping grain follows the speed at its start.</summary>
    internal static int[] Speed(int[] x, int[] percent, int rate)
    {
        if (percent.Length == 0 || percent.All(value => value == 100)) return x;
        int grain = Math.Max(64, rate / 20), hopOut = grain / 2;
        int[] window = new int[grain];
        for (int i = 0; i < grain; i++) window[i] = (int)(32768L * Math.Min(i, grain - i) * 2 / grain);
        int[] y = new int[x.Length];
        long inPosQ16 = 0;
        for (int outPos = 0; outPos < y.Length; outPos += hopOut)
        {
            int inPos = (int)(inPosQ16 >> 16);
            if (inPos >= x.Length) break;
            for (int i = 0; i < grain && outPos + i < y.Length; i++)
                if (inPos + i < x.Length) y[outPos + i] += (int)((long)x[inPos + i] * window[i] >> 15);
            inPosQ16 += (long)hopOut * Math.Clamp(percent[Math.Min(outPos, percent.Length - 1)], 25, 400) * 65536 / 100;
        }

        return y;
    }

    /// <summary>The ways to shift a pitch: 0 granular (the first one: simple, grainy), 1 wsola (grains that are lined up with each other: cleaner, for instruments and sounds), 2 voice (psola: one pitch period at a time, keeps the character of a voice).</summary>
    internal const int MethodGranular = 0, MethodWsola = 1, MethodVoice = 2;

    /// <summary>Moves the pitch by hundredths of a semitone (100 is one semitone, 1200 an octave) and keeps the length.</summary>
    internal static int[] PitchShift(int[] x, int cents, int rate, int method = MethodGranular)
    {
        int clamped = Math.Clamp(cents, -2400, 2400);
        if (clamped == 0)
        {
            return x;
        }

        long ratio = RatioQ16Cents(clamped);
        if (method == MethodVoice)
        {
            return Psola(x, ratio, rate);
        }

        int stretchedLength = (int)Math.Min(int.MaxValue / 2, (long)x.Length * ratio >> 16) + 2;
        int[] stretched = method == MethodWsola ? StretchWsola(x, stretchedLength, 65536L * 65536 / ratio, rate) : Stretch(x, stretchedLength, 65536L * 65536 / ratio, rate);
        int[] y = new int[x.Length];
        for (int i = 0; i < y.Length; i++)
        {
            long position = i * ratio;
            int index = (int)(position >> 16), fraction = (int)(position & 0xFFFF);
            if (index + 1 >= stretched.Length)
            {
                break;
            }

            y[i] = (int)(((long)stretched[index] * (65536 - fraction)) + ((long)stretched[index + 1] * fraction) >> 16);
        }

        return y;
    }

    private static int[] Stretch(int[] x, int outputLength, long speedQ16, int rate)
    {
        int grain = Math.Max(64, rate / 20), hopOut = grain / 2;
        long hopIn = hopOut * speedQ16 >> 16;
        int[] window = new int[grain];
        for (int i = 0; i < grain; i++)
        {
            window[i] = (int)(32768L * Math.Min(i, grain - i) * 2 / grain);
        }

        int[] y = new int[outputLength];
        for (long count = 0, outPos = 0; outPos < outputLength; count++, outPos += hopOut)
        {
            long inPos = count * hopIn;
            if (inPos >= x.Length)
            {
                break;
            }

            for (int i = 0; i < grain; i++)
            {
                long o = outPos + i, s = inPos + i;
                if (o >= outputLength)
                {
                    break;
                }

                if (s < x.Length)
                {
                    y[o] += (int)((long)x[s] * window[i] >> 15);
                }
            }
        }

        return y;
    }

    /// <summary>
    /// Waveform similarity overlap-add: like the granular stretch, but every grain is moved a little (within 5 ms) to the place where it continues the waveform of the one before best,
    /// so the grains do not fight each other and the metallic sound goes away. Integer only.
    /// </summary>
    private static int[] StretchWsola(int[] x, int outputLength, long speedQ16, int rate)
    {
        int grain = Math.Max(256, rate / 25), hopOut = grain / 2, reach = Math.Max(16, rate / 200), step = 4;
        long hopIn = hopOut * speedQ16 >> 16;
        int[] window = new int[grain];
        for (int i = 0; i < grain; i++)
        {
            window[i] = (int)(32768L * Math.Min(i, grain - i) * 2 / grain);
        }

        int[] y = new int[outputLength];
        long previous = 0;
        bool first = true;
        for (long count = 0, outPos = 0; outPos < outputLength; count++, outPos += hopOut)
        {
            long nominal = count * hopIn, best = nominal;
            if (nominal >= x.Length)
            {
                break;
            }

            if (!first)
            {
                // the natural continuation of the grain that was laid down before, compared with the candidates around the place where the next grain should come from
                long target = previous + hopOut, bestScore = long.MinValue;
                for (long candidate = Math.Max(0, nominal - reach); candidate <= nominal + reach; candidate += 1)
                {
                    long score = 0;
                    for (int i = 0; i < grain / 2; i += step)
                    {
                        long a = target + i, b = candidate + i;
                        if (a >= x.Length || b >= x.Length)
                        {
                            break;
                        }

                        score += (long)x[a] * x[b];
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }
            }

            first = false;
            previous = best;
            for (int i = 0; i < grain; i++)
            {
                long o = outPos + i, src = best + i;
                if (o >= outputLength)
                {
                    break;
                }

                if (src < x.Length)
                {
                    y[o] += (int)((long)x[src] * window[i] >> 15);
                }
            }
        }

        return y;
    }

    /// <summary>
    /// Pitch synchronous overlap-add, for a voice: the period of the voice is followed (autocorrelation, 70 to 800 Hz), a window of two periods is taken around every pitch mark and laid down
    /// at a distance of one period divided by the ratio. The sound keeps the length, and the vowels keep their colour (the formants), where resampling would make a chipmunk. Where the
    /// sound is not voiced (breath, consonants, silence) a fixed period of 10 ms is used. Integer only.
    /// </summary>
    private static int[] Psola(int[] x, long ratioQ16, int rate)
    {
        int frame = Math.Max(1, rate / 100), minLag = Math.Max(2, rate / 800), maxLag = Math.Max(minLag + 2, rate / 70), window = Math.Max(maxLag * 2, rate / 25);
        int frames = x.Length / frame + 1;
        int[] period = new int[frames];
        int fallback = rate / 100;
        for (int f = 0; f < frames; f++)
        {
            long start = (long)f * frame - (window / 2);
            long bestScore = 0, energy = 0;
            int bestLag = 0;
            int to = (int)Math.Min(x.Length - 1, start + window), from = (int)Math.Max(0, start);
            for (int i = from; i <= to; i += 2)
            {
                energy += (long)x[i] * x[i];
            }

            if (energy > 0)
            {
                for (int lag = minLag; lag <= maxLag; lag++)
                {
                    long sum = 0;
                    for (int i = from; i + lag <= to; i += 2)
                    {
                        sum += (long)x[i] * x[i + lag];
                    }

                    if (sum > bestScore)
                    {
                        bestScore = sum;
                        bestLag = lag;
                    }
                }
            }

            // voiced when the best lag explains enough of the energy
            period[f] = bestLag > 0 && bestScore * 10 > energy * 3 ? bestLag : fallback;
        }

        int PeriodAt(long position) => period[(int)Math.Clamp(position / frame, 0, frames - 1)];

        // the pitch marks: one period apart, each moved to the strongest sample close to where it should be
        List<int> marks = [];
        long mark = PeriodAt(0);
        while (mark < x.Length)
        {
            int p = PeriodAt(mark), lo = (int)Math.Max(0, mark - (p / 4)), hi = (int)Math.Min(x.Length - 1, mark + (p / 4)), peak = (int)mark;
            if (p != fallback)
            {
                for (int i = lo; i <= hi; i++)
                {
                    if (Math.Abs(x[i]) > Math.Abs(x[peak])) peak = i;
                }
            }

            marks.Add(peak);
            int next = peak + PeriodAt(peak);
            mark = next > mark ? next : mark + Math.Max(2, PeriodAt(peak));   // the period can change at once; never step back
            if (marks.Count > x.Length) break;
        }

        if (marks.Count == 0)
        {
            return x;
        }

        int[] y = new int[x.Length];
        long gain = 65536L * 65536 / ratioQ16;   // windows of two periods laid down closer (or further) than a period add up to the ratio
        long at = marks[0];
        while (at < x.Length)
        {
            int nearest = 0, bestDistance = int.MaxValue;
            int lowIndex = 0, highIndex = marks.Count - 1;
            while (lowIndex <= highIndex)
            {
                int mid = (lowIndex + highIndex) / 2;
                int distance = Math.Abs(marks[mid] - (int)at);
                if (distance < bestDistance) { bestDistance = distance; nearest = mid; }
                if (marks[mid] < at) lowIndex = mid + 1; else highIndex = mid - 1;
            }

            int center = marks[nearest], p = Math.Max(2, PeriodAt(center));
            for (int i = -p; i < p; i++)
            {
                long o = at + i, src = center + i;
                if (o < 0 || o >= y.Length || src < 0 || src >= x.Length)
                {
                    continue;
                }

                long weight = 32768L * (p - Math.Abs(i)) / p;   // triangular window of two periods
                y[o] += (int)(((long)x[src] * weight >> 15) * gain >> 16);
            }

            at += Math.Max(1, (p * 65536L / ratioQ16));
        }

        return y;
    }
}
