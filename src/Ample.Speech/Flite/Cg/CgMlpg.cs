namespace Ample.Speech.Flite.Cg;

// Port of Flite (CMU, commit 6c9f20dc), src/cg/cst_mlpg.c (mlpg, xget_detvec_diamat2inv, get_like_pdfseq_vit (the part that makes the PDF), InitDWin, InitPStreamChol,
// mlgparaChol, mlpgChol, calc_R_and_r, Choleski, Choleski_forward, Choleski_backward). Translated to C# and modified.
// Original: Tomoki Toda, Nagoya Institute of Technology (June 2004), modified as a single file for Festival and Flite by Alan W Black, Carnegie Mellon University (May 2008).
// Copyright and licence: LICENSES/flite.txt (Nagoya Institute of Technology, Carnegie Mellon University). The likelihood and the global variance code of the original, whose results
// the output does not depend on, are not part of this port.

/// <summary>Maximum likelihood parameter generation (MLPG): the means and standard deviations of the static and delta mel-cepstrum of every frame become one smooth static trajectory
/// (a banded Cholesky solve for each coefficient). All arithmetic is double precision with <c>+ - * /</c> and <c>sqrt</c>, as in C.</summary>
internal static class CgMlpg
{
    /// <summary>The magic covariance of the original: a frame whose determinant is not positive takes this standard deviation (0.0962) for the coefficient.</summary>
    private const double MagicCovariance = 0.0962;

    /// <summary>The width of the R band: the window of the deltas reaches one frame to each side (3).</summary>
    private const int Width = 3;

    /// <summary>The columns of the output track: F0 and the 25 smoothed static coefficients.</summary>
    public const int OutputColumns = 26;

    /// <summary>The smoothed track: column 0 is the F0 of <paramref name="track"/>, columns 1 to 25 the smoothed static mel-cepstrum.</summary>
    /// <param name="track">The parameter track after the F0 smoothing: F0, an unused column, then 50 mean and standard deviation pairs.</param>
    /// <param name="dynamicWindow">The window of the delta coefficients (three values: -0.5, 0, 0.5).</param>
    /// <param name="cancellationToken">Stops the work between two coefficients.</param>
    public static float[][] Generate(CgParamTrack track, ReadOnlySpan<float> dynamicWindow, CancellationToken cancellationToken)
    {
        float[][] frames = track.Frames;
        int count = frames.Length;
        int dim = (CgParamTrack.Columns / 2) - 1;   // 50: static and delta
        int dimStatic = dim / 2;                    // 25
        int order = dimStatic - 1;
        if (dynamicWindow.Length != 3 || dynamicWindow[1] != 0f || dynamicWindow[0] != -dynamicWindow[2]) throw new InvalidDataException("The dynamic window is not the one of the clustergen voices.");
        float[][] output = new float[count][];
        if (count == 0) return output;

        // InitDWin: the window of the deltas: coefficients for -1, 0 and 1 of the frame (the static window is the identity)
        double[] delta = [dynamicWindow[0], dynamicWindow[1], dynamicWindow[2]];
        double Coef(int k) => delta[k + 1];

        // the PDF of every frame: U^-1 * M and U^-1 of the diagonal covariance (xget_detvec_diamat2inv and the pdf part of get_like_pdfseq_vit)
        double[][] mseq = new double[count][], ivseq = new double[count][];
        double[] magic = MagicPowers(dim);
        for (int i = 0; i < count; i++)
        {
            float[] row = frames[i];
            double[] covariance = new double[dim];
            for (int j = 0; j < dim; j++) covariance[j] = row[((j + 1) * 2) + 1] * row[((j + 1) * 2) + 1];   // a product of two floats, then widened
            double det = 1.0;
            for (int j = 0; j < dim; j++)
            {
                det *= covariance[j];
                if (det > 0.0)
                {
                    covariance[j] = 1.0 / covariance[j];
                }
                else
                {
                    covariance[j] = 1.0 / MagicCovariance;
                    det = magic[j];   // pow(magic_covariance, j + 1)
                }
            }

            double[] m = mseq[i] = new double[dim], iv = ivseq[i] = new double[dim];
            for (int k = 0; k < dim; k++)
            {
                m[k] = covariance[k] * (double)row[((k + 1) * 2)];
                iv[k] = covariance[k];
            }
        }

        // InitPStreamChol: R is a band of three values for each frame
        double[][] R = new double[count][];
        for (int i = 0; i < count; i++) R[i] = new double[Width];
        double[] r = new double[count], g = new double[count];
        double[][] c = new double[count][];
        for (int i = 0; i < count; i++) c[i] = new double[order + 1];

        for (int m = 0; m <= order; m++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CalcRAndR(count, order, m, mseq, ivseq, R, r, Coef);
            Choleski(count, R);
            CholeskiForward(count, R, r, g);
            CholeskiBackward(count, R, g, c, m);
        }

        for (int i = 0; i < count; i++)
        {
            float[] row = output[i] = new float[OutputColumns];
            row[0] = frames[i][0];   // F0
            for (int j = 0; j < dimStatic; j++) row[j + 1] = (float)c[i][j];
        }

        return output;
    }

    /// <summary>pow(0.0962, j + 1) for j = 0 to <paramref name="count"/> - 1 by repeated multiplication: the value only decides whether the next running product is positive.</summary>
    private static double[] MagicPowers(int count)
    {
        double[] powers = new double[count];
        double value = 1.0;
        for (int j = 0; j < count; j++)
        {
            value *= MagicCovariance;
            powers[j] = value;
        }

        return powers;
    }

    /// <summary>R = W' U^-1 W and r = W' U^-1 M for one coefficient, with the band of the static and the delta window.</summary>
    private static void CalcRAndR(int count, int order, int m, double[][] mseq, double[][] ivseq, double[][] R, double[] r, Func<int, double> coef)
    {
        for (int i = 0; i < count; i++)
        {
            r[i] = mseq[i][m];
            R[i][0] = ivseq[i][m];
            for (int j = 1; j < Width; j++) R[i][j] = 0.0;
            for (int k = -1; k <= 1; k++)   // the delta window: width[1][0] = -1 to width[1][1] = 1
            {
                int n = i + k;
                if (n >= 0 && n < count && coef(-k) != 0.0)
                {
                    int l = 1 * (order + 1) + m;
                    r[i] += coef(-k) * mseq[n][l];
                    double wu = coef(-k) * ivseq[n][l];
                    for (l = 0; l < Width; l++)
                    {
                        n = l - k;
                        if (n <= 1 && i + l < count && coef(n) != 0.0) R[i][l] += wu * coef(n);
                    }
                }
            }
        }
    }

    /// <summary>The Cholesky factorization of the band matrix R.</summary>
    private static void Choleski(int count, double[][] R)
    {
        R[0][0] = Math.Sqrt(R[0][0]);
        for (int j = 1; j < Width; j++) R[0][j] /= R[0][0];
        for (int t = 1; t < count; t++)
        {
            for (int j = 1; j < Width; j++)
            {
                if (t - j >= 0) R[t][0] -= R[t - j][j] * R[t - j][j];
            }

            R[t][0] = Math.Sqrt(R[t][0]);
            for (int j = 1; j < Width; j++)
            {
                for (int k = 0; k < 1; k++)   // k < maxw[WRIGHT]
                {
                    if (j != Width - 1) R[t][j] -= R[t - k - 1][j - k] * R[t - k - 1][j + 1];
                }

                R[t][j] /= R[t][0];
            }
        }
    }

    private static void CholeskiForward(int count, double[][] R, double[] r, double[] g)
    {
        g[0] = r[0] / R[0][0];
        for (int t = 1; t < count; t++)
        {
            double hold = 0.0;
            for (int j = 1; j < Width; j++)
            {
                if (t - j >= 0 && R[t - j][j] != 0.0) hold += R[t - j][j] * g[t - j];
            }

            g[t] = (r[t] - hold) / R[t][0];
        }
    }

    private static void CholeskiBackward(int count, double[][] R, double[] g, double[][] c, int m)
    {
        c[count - 1][m] = g[count - 1] / R[count - 1][0];
        for (int t = count - 2; t >= 0; t--)
        {
            double hold = 0.0;
            for (int j = 1; j < Width; j++)
            {
                if (t + j < count && R[t][j] != 0.0) hold += R[t][j] * c[t + j][m];
            }

            c[t][m] = (g[t] - hold) / R[t][0];
        }
    }
}
