using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LTools.Features.CapturePro.Stitch;

/// <summary>把一帧切成均匀的小块（SSIM 与特征分布都按块算）。</summary>
/// <remarks>移植自 <c>snow-stitch-images/src/region.rs</c> 的 <c>TileLayout</c>。</remarks>
internal readonly struct TileLayout
{
    public TileLayout(int width, int height, int tileSize)
    {
        Width = width;
        Height = height;
        TileSize = tileSize;
        Columns = (width + tileSize - 1) / tileSize;
        Rows = (height + tileSize - 1) / tileSize;
    }

    public int Width { get; }

    public int Height { get; }

    public int TileSize { get; }

    public int Columns { get; }

    public int Rows { get; }

    public int Count => Columns * Rows;

    /// <summary>某个坐标落在第几块；越界返回 <c>null</c>。</summary>
    public int? Index(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0f || y < 0f) return null;

        int column = (int)MathF.Floor(x) / TileSize;
        int row = (int)MathF.Floor(y) / TileSize;
        if (column >= Columns || row >= Rows) return null;
        return row * Columns + column;
    }

    /// <summary>第 <paramref name="index"/> 块的像素范围 [x0, x1) × [y0, y1)。</summary>
    public (int X0, int Y0, int X1, int Y1) Bounds(int index)
    {
        int column = index % Columns;
        int row = index / Columns;
        int x0 = column * TileSize;
        int y0 = row * TileSize;
        return (x0, y0, Math.Min(x0 + TileSize, Width), Math.Min(y0 + TileSize, Height));
    }
}

/// <summary>4 倍降采样 + 3x3 模糊 + Sobel 梯度：SSIM 与纹理权重都在这张图上算。</summary>
/// <remarks>移植自 <c>region.rs</c> 的 <c>GrayImage</c>（常量 <c>DOWNSAMPLE = 4</c>）。</remarks>
internal sealed class GrayImage
{
    public const int Downsample = 4;

    private GrayImage(int width, int height, float[] pixels, float[] gradients)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        Gradients = gradients;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>降采样 + 模糊后的灰度（0–255）。</summary>
    public float[] Pixels { get; }

    /// <summary>Sobel 梯度幅值（0–255）。</summary>
    public float[] Gradients { get; }

    public static GrayImage FromFrame(StitchFrame frame)
    {
        int width = (frame.Width + Downsample - 1) / Downsample;
        int height = (frame.Height + Downsample - 1) / Downsample;
        var downsampled = new float[width * height];

        Parallel.For(0, height, scaledY =>
        {
            int y0 = scaledY * Downsample;
            int y1 = Math.Min(y0 + Downsample, frame.Height);
            int rowStart = scaledY * width;
            byte[] source = frame.Pixels;
            for (int scaledX = 0; scaledX < width; scaledX++)
            {
                int x0 = scaledX * Downsample;
                int x1 = Math.Min(x0 + Downsample, frame.Width);
                float sum = 0f;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    int offset = (y * frame.Width + x0) * StitchFrame.Channels;
                    for (int x = x0; x < x1; x++, offset += StitchFrame.Channels)
                    {
                        // Bgra32：byte0 = B、1 = G、2 = R。
                        sum += 0.299f * source[offset + 2] + 0.587f * source[offset + 1] + 0.114f * source[offset];
                        count++;
                    }
                }

                downsampled[rowStart + scaledX] = sum / Math.Max(1, count);
            }
        });

        float[] blurred = Blur3x3(downsampled, width, height);
        float[] gradients = SobelMagnitude(blurred, width, height);
        return new GrayImage(width, height, blurred, gradients);
    }

    private static float[] Blur3x3(float[] source, int width, int height)
    {
        var output = new float[source.Length];
        if (width == 0 || height == 0) return output;

        Parallel.For(0, height, y =>
        {
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                if (y > 0 && y + 1 < height && x > 0 && x + 1 < width)
                {
                    int top = (y - 1) * width;
                    int middle = y * width;
                    int bottom = (y + 1) * width;
                    float sum = source[top + x - 1];
                    sum += source[top + x] * 2f;
                    sum += source[top + x + 1];
                    sum += source[middle + x - 1] * 2f;
                    sum += source[middle + x] * 4f;
                    sum += source[middle + x + 1] * 2f;
                    sum += source[bottom + x - 1];
                    sum += source[bottom + x] * 2f;
                    sum += source[bottom + x + 1];
                    output[rowStart + x] = sum / 16f;
                    continue;
                }

                float borderSum = 0f;
                float total = 0f;
                for (int kernelY = 0; kernelY < 3; kernelY++)
                {
                    int sampleY = Math.Clamp(y + kernelY - 1, 0, Math.Max(0, height - 1));
                    float weightY = kernelY == 1 ? 2f : 1f;
                    for (int kernelX = 0; kernelX < 3; kernelX++)
                    {
                        int sampleX = Math.Clamp(x + kernelX - 1, 0, Math.Max(0, width - 1));
                        float weight = (kernelX == 1 ? 2f : 1f) * weightY;
                        borderSum += source[sampleY * width + sampleX] * weight;
                        total += weight;
                    }
                }

                output[rowStart + x] = borderSum / total;
            }
        });

        return output;
    }

    private static float[] SobelMagnitude(float[] source, int width, int height)
    {
        var output = new float[source.Length];
        if (width < 3 || height < 3) return output;

        Parallel.For(1, height - 1, y =>
        {
            int top = (y - 1) * width;
            int middle = y * width;
            int bottom = (y + 1) * width;
            for (int x = 1; x < width - 1; x++)
            {
                float gx = -source[top + x - 1] + source[top + x + 1]
                    - 2f * source[middle + x - 1] + 2f * source[middle + x + 1]
                    - source[bottom + x - 1] + source[bottom + x + 1];
                float gy = -source[top + x - 1] - 2f * source[top + x] - source[top + x + 1]
                    + source[bottom + x - 1] + 2f * source[bottom + x] + source[bottom + x + 1];
                output[middle + x] = MathF.Min(MathF.Sqrt(gx * gx + gy * gy), 255f);
            }
        });

        return output;
    }
}

/// <summary>逐块相似度 + 纹理强度：候选位移的「配准误差」与最终置信度都从这里来。</summary>
/// <remarks>
/// 移植自 <c>region.rs</c> 的 <c>SimilarityMap</c>。单块相似度 = 0.7 × SSIM + 0.3 × 梯度相似度
/// （SSIM 的 <c>C1 = 6.5025</c>、<c>C2 = 58.5225</c>，与 OpenCV 的 8 位灰度取值一致），
/// 纹理强度把「方差」和「平均梯度」各取一半，用来给块加权——纯色块说明不了什么。
/// </remarks>
internal sealed class SimilarityMap
{
    private SimilarityMap(TileLayout layout, float[] values, float[] texture, bool[] valid)
    {
        Layout = layout;
        Values = values;
        Texture = texture;
        Valid = valid;
    }

    public TileLayout Layout { get; }

    public float[] Values { get; }

    public float[] Texture { get; }

    public bool[] Valid { get; }

    public static SimilarityMap Between(GrayImage reference, GrayImage incoming, TileLayout layout, StitchAxis axis, int offset)
    {
        float scaledOffset = offset / (float)GrayImage.Downsample;
        var values = new float[layout.Count];
        var texture = new float[layout.Count];
        var valid = new bool[layout.Count];

        Parallel.For(0, layout.Count, tile =>
        {
            if (!SimilarityForTile(reference, incoming, layout, axis, scaledOffset, tile, out float value, out float weight))
                return;

            values[tile] = value;
            texture[tile] = weight;
            valid[tile] = true;
        });

        return MedianFiltered(layout, values, texture, valid);
    }

    private static bool SimilarityForTile(
        GrayImage reference, GrayImage incoming, TileLayout layout, StitchAxis axis,
        float scaledOffset, int tile, out float value, out float texture)
    {
        value = 0f;
        texture = 0f;

        var (x0, y0, x1, y1) = layout.Bounds(tile);
        int scaledX0 = x0 / GrayImage.Downsample;
        int scaledY0 = y0 / GrayImage.Downsample;
        int scaledX1 = (x1 + GrayImage.Downsample - 1) / GrayImage.Downsample;
        int scaledY1 = (y1 + GrayImage.Downsample - 1) / GrayImage.Downsample;
        long expected = (long)(scaledX1 - scaledX0) * (scaledY1 - scaledY0);

        float[] referencePixels = reference.Pixels;
        float[] incomingPixels = incoming.Pixels;
        int limitY = Math.Min(scaledY1, incoming.Height);
        int limitX = Math.Min(scaledX1, incoming.Width);

        int sampleCount = 0;
        float referenceSum = 0f;
        float incomingSum = 0f;
        for (int y = scaledY0; y < limitY; y++)
        {
            int incomingRow = y * incoming.Width;
            for (int x = scaledX0; x < limitX; x++)
            {
                if (!BilinearAxisSample(referencePixels, reference.Width, reference.Height,
                        axis == StitchAxis.Vertical ? x : x - scaledOffset,
                        axis == StitchAxis.Vertical ? y - scaledOffset : y,
                        out float referenceValue))
                {
                    continue;
                }

                referenceSum += referenceValue;
                incomingSum += incomingPixels[incomingRow + x];
                sampleCount++;
            }
        }

        if (sampleCount < Math.Max((expected + 1) / 2, 4)) return false;

        float count = sampleCount;
        float meanReference = referenceSum / count;
        float meanIncoming = incomingSum / count;
        float referenceVariance = 0f;
        float incomingVariance = 0f;
        float covariance = 0f;
        float gradientError = 0f;
        float meanGradient = 0f;

        for (int y = scaledY0; y < limitY; y++)
        {
            int incomingRow = y * incoming.Width;
            for (int x = scaledX0; x < limitX; x++)
            {
                float sampleX = axis == StitchAxis.Vertical ? x : x - scaledOffset;
                float sampleY = axis == StitchAxis.Vertical ? y - scaledOffset : y;
                if (!BilinearAxisSample(referencePixels, reference.Width, reference.Height, sampleX, sampleY, out float referenceValue))
                    continue;
                if (!BilinearAxisSample(reference.Gradients, reference.Width, reference.Height, sampleX, sampleY, out float referenceGradient))
                    return false;

                int index = incomingRow + x;
                float incomingValue = incomingPixels[index];
                float incomingGradient = incoming.Gradients[index];
                float referenceDelta = referenceValue - meanReference;
                float incomingDelta = incomingValue - meanIncoming;
                referenceVariance += referenceDelta * referenceDelta;
                incomingVariance += incomingDelta * incomingDelta;
                covariance += referenceDelta * incomingDelta;
                gradientError += MathF.Abs(referenceGradient - incomingGradient);
                meanGradient += MathF.Max(referenceGradient, incomingGradient);
            }
        }

        referenceVariance /= count;
        incomingVariance /= count;
        covariance /= count;
        gradientError /= count;
        meanGradient /= count;

        const float c1 = 6.5025f;
        const float c2 = 58.5225f;
        float numerator = (2f * meanReference * meanIncoming + c1) * (2f * covariance + c2);
        float denominator = (meanReference * meanReference + meanIncoming * meanIncoming + c1)
                            * (referenceVariance + incomingVariance + c2);
        float ssim = denominator > float.Epsilon ? Math.Clamp(numerator / denominator, 0f, 1f) : 1f;
        float gradientSimilarity = Math.Clamp(1f - gradientError / 64f, 0f, 1f);
        value = 0.7f * ssim + 0.3f * gradientSimilarity;
        texture = Math.Clamp(
            (MathF.Sqrt(MathF.Max(referenceVariance, incomingVariance)) / 32f + meanGradient / 64f) * 0.5f,
            0f, 1f);
        return true;
    }

    /// <summary>
    /// 沿轴的双线性采样：纵向只在 y 上插值、横向只在 x 上插值（另一维保持整数），与 Snow Shot 一致。
    /// </summary>
    private static bool BilinearAxisSample(float[] pixels, int width, int height, float x, float y, out float sample)
    {
        sample = 0f;
        if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0f || y < 0f ||
            x > Math.Max(0, width - 1) || y > Math.Max(0, height - 1))
        {
            return false;
        }

        int column = (int)x;
        int row = (int)y;
        if (width == 0 || height == 0) return false;

        // 两个轴都按「整数取整 + 轴上插值」处理：非轴那一维的小数部分恒为 0，等价于取整。
        if (x - column > 0f)
        {
            int right = Math.Min(column + 1, width - 1);
            float fraction = x - column;
            int index = row * width + column;
            sample = pixels[index] * (1f - fraction) + pixels[row * width + right] * fraction;
            return true;
        }

        if (y - row > 0f)
        {
            int bottom = Math.Min(row + 1, height - 1);
            float fraction = y - row;
            sample = pixels[row * width + column] * (1f - fraction) + pixels[bottom * width + column] * fraction;
            return true;
        }

        sample = pixels[row * width + column];
        return true;
    }

    private static SimilarityMap MedianFiltered(TileLayout layout, float[] values, float[] texture, bool[] valid)
    {
        var source = (float[])values.Clone();
        var filtered = (float[])values.Clone();
        int columns = layout.Columns;
        int rows = layout.Rows;
        var neighbors = new float[9];

        for (int index = 0; index < layout.Count; index++)
        {
            if (!valid[index]) continue;

            int column = index % columns;
            int row = index / columns;
            int firstColumn = Math.Max(0, column - 1);
            int lastColumn = Math.Min(column + 1, columns - 1);
            int firstRow = Math.Max(0, row - 1);
            int lastRow = Math.Min(row + 1, rows - 1);

            int count = 0;
            for (int neighborRow = firstRow; neighborRow <= lastRow; neighborRow++)
            {
                int rowStart = neighborRow * columns;
                for (int neighborColumn = firstColumn; neighborColumn <= lastColumn; neighborColumn++)
                {
                    int neighbor = rowStart + neighborColumn;
                    if (valid[neighbor]) neighbors[count++] = source[neighbor];
                }
            }

            if (count == 9)
            {
                Array.Sort(neighbors);
                filtered[index] = neighbors[4];
            }
            else
            {
                var slice = new float[count];
                Array.Copy(neighbors, slice, count);
                Array.Sort(slice);
                filtered[index] = slice[count / 2];
            }
        }

        return new SimilarityMap(layout, filtered, texture, valid);
    }

    public float? AtTile(int index)
    {
        if (index < 0 || index >= Valid.Length || !Valid[index]) return null;
        return Values[index];
    }

    public float? AtPoint(float x, float y)
    {
        int? index = Layout.Index(x, y);
        return index is int value ? AtTile(value) : null;
    }

    /// <summary>按纹理加权的平均相似度（纯色块只算 0.1 的权重）。</summary>
    public float Mean()
    {
        float weightedSum = 0f;
        float total = 0f;
        for (int index = 0; index < Layout.Count; index++)
        {
            if (!Valid[index]) continue;
            float weight = 0.1f + 0.9f * Texture[index];
            weightedSum += Values[index] * weight;
            total += weight;
        }

        return total > 0f ? weightedSum / total : 0f;
    }

    public float TextureAt(int index) => index >= 0 && index < Texture.Length ? Texture[index] : 0f;
}
