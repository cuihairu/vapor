using System.Text;

// CA1814 (prefer jagged arrays): the module grid is a genuine square matrix
// (≤ 41×41, symmetric [row, col] access everywhere) — jagged rows would add
// allocation ceremony without any perf win at this size.
#pragma warning disable CA1814

namespace Vapor.ControlPlane;

/// <summary>
/// Minimal QR encoder (ISO/IEC 18004 subset) for first-party console rendering.
/// Deliberate scope: byte mode, ECC level M, versions 1-6 — up to 106 UTF-8
/// bytes, which covers Steam QR-login challenge URLs ("https://s.team/q/1/…").
///
/// Why in-repo instead of alternatives (todo §18 deferral, resolved here):
/// QRCoder would break the zero-new-NuGet rule, an embedded JS encoder has no
/// test guard (static assets are outside the coverage ratchet), and a
/// third-party image service would send the login token off-box (security red
/// line). A server-side render keeps the token inside the control plane and
/// puts every encoder line under the double-100% coverage gate.
///
/// Algorithm shape follows the well-known reference implementations of the
/// standard (function patterns, zigzag placement, BCH format info with
/// generator 0x537 masked by 0x5412, GF(256) Reed-Solomon with primitive
/// polynomial 0x11D, the eight data masks); penalty rule 3 scans in-bounds
/// 11-module windows only (edge-truncated finder-like patterns are not
/// counted — a documented simplification that affects mask aesthetics, not
/// decodability, since decoders read the mask from the format bits).
/// </summary>
internal static class QrEncoder
{
	/// <summary>Version 6-M byte-mode capacity; the largest payload this encoder accepts.</summary>
	public const int MaxByteLength = 106;

	// (module size, total data codewords, RS blocks, EC codewords per block)
	// for versions 1-6 at ECC level M. Blocks are equal-sized at every listed
	// version, so no unequal-block split handling is needed. Data capacities
	// (16/28/44/64/86/108 codewords − 2 for mode+length+terminator) yield the
	// published byte-mode capacities 14/26/42/62/84/106.
	private static readonly (int Size, int DataCodewords, int Blocks, int EcPerBlock)[] Versions =
	{
		(21, 16, 1, 10),
		(25, 28, 1, 16),
		(29, 44, 1, 26),
		(33, 64, 2, 18),
		(37, 86, 2, 24),
		(41, 108, 4, 16),
	};

	/// <summary>Encodes <paramref name="text"/> (UTF-8) into a module matrix (true = dark).</summary>
	/// <exception cref="ArgumentException">text is null/blank or exceeds <see cref="MaxByteLength"/> UTF-8 bytes.</exception>
	internal static bool[,] Encode(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new ArgumentException("text is required", nameof(text));
		}

		byte[] payload = Encoding.UTF8.GetBytes(text);
		if (payload.Length > MaxByteLength)
		{
			throw new ArgumentException(
				$"text exceeds the {MaxByteLength}-byte QR capacity (byte mode, ECC level M, versions 1-6)", nameof(text));
		}

		// Smallest version whose data codewords fit mode(4) + length(8) + payload + terminator(4).
		// Guaranteed to stop: payload.Length <= 106 means version 6 (108 codewords) always fits.
		int index = 0;
		while (Versions[index].DataCodewords < payload.Length + 2)
		{
			index++;
		}

		(int size, int dataCodewords, int blocks, int ecPerBlock) = Versions[index];

		byte[] data = BuildDataCodewords(payload, dataCodewords);
		byte[][] dataBlocks = SplitEqual(data, blocks);
		byte[][] ecBlocks = dataBlocks.Select(block => ReedSolomonRemainder(block, ecPerBlock)).ToArray();
		byte[] codewords = Interleave(dataBlocks, ecBlocks);

		bool[,] modules = new bool[size, size];
		bool[,] functions = new bool[size, size];
		DrawFinders(modules, functions, size);
		DrawTiming(modules, functions, size);
		if (index >= 1)
		{
			// Versions 2-6 carry exactly one alignment pattern (the {6, size-7}
			// grid minus finder overlaps leaves only the (size-7, size-7) cell).
			DrawAlignment(modules, functions, size);
		}

		modules[size - 8, 8] = true; // the always-dark module above the bottom-left finder
		ReserveFormat(functions, size);
		functions[size - 8, 8] = true; // the dark module is function space too — never receives data bits
		PlaceCodewords(modules, functions, size, codewords);

		bool[,] best = new bool[size, size];
		long bestScore = long.MaxValue;
		for (int mask = 0; mask < 8; mask++)
		{
			bool[,] candidate = (bool[,])modules.Clone();
			ApplyMask(candidate, functions, size, mask);
			DrawFormatBits(candidate, size, mask);
			long score = PenaltyScore(candidate);
			if (score < bestScore)
			{
				bestScore = score;
				best = candidate;
			}
		}

		return best;
	}

	/// <summary>Renders the module matrix as a self-contained SVG with a quiet zone.</summary>
	internal static string ToSvg(bool[,] matrix, int scale = 4, int border = 4)
	{
		int size = matrix.GetLength(0);
		int total = size + 2 * border;
		var sb = new StringBuilder();
		sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(total).Append(' ')
			.Append(total).Append("\" width=\"").Append(total * scale).Append("\" height=\"").Append(total * scale)
			.Append("\" shape-rendering=\"crispEdges\">");
		sb.Append("<rect width=\"").Append(total).Append("\" height=\"").Append(total).Append("\" fill=\"#ffffff\"/>");
		sb.Append("<path d=\"");
		for (int row = 0; row < size; row++)
		{
			for (int col = 0; col < size; col++)
			{
				if (matrix[row, col])
				{
					sb.Append('M').Append(col + border).Append(' ').Append(row + border).Append("h1v1h-1z");
				}
			}
		}

		sb.Append("\" fill=\"#000000\"/></svg>");
		return sb.ToString();
	}

	/// <summary>
	/// Builds the data codewords: mode segment, 8-bit length field (byte mode
	/// uses 8 length bits through version 9), payload, terminator, 0xEC/0x11 pad.
	/// In this domain mode(4)+length(8)+payload+terminator(4) is always a whole
	/// number of bytes, so no bit-alignment padding exists.
	/// </summary>
	internal static byte[] BuildDataCodewords(byte[] payload, int dataCodewords)
	{
		var bits = new List<bool>(dataCodewords * 8);
		AppendBits(bits, 0b0100, 4);
		AppendBits(bits, payload.Length, 8);
		foreach (byte b in payload)
		{
			AppendBits(bits, b, 8);
		}

		AppendBits(bits, 0, Math.Min(4, dataCodewords * 8 - bits.Count));
		byte[] result = new byte[dataCodewords];
		for (int i = 0; i < bits.Count / 8; i++)
		{
			int value = 0;
			for (int j = 0; j < 8; j++)
			{
				if (bits[i * 8 + j])
				{
					value |= 1 << (7 - j);
				}
			}

			result[i] = (byte)value;
		}

		bool alternate = false; // pad codewords alternate 0xEC, 0x11, 0xEC, ...
		for (int i = bits.Count / 8; i < dataCodewords; i++, alternate = !alternate)
		{
			result[i] = alternate ? (byte)0x11 : (byte)0xEC;
		}

		return result;
	}

	/// <summary>
	/// Systematic Reed-Solomon remainder of <paramref name="data"/> for codes with
	/// <paramref name="ecLength"/> error-correction codewords over GF(256)
	/// (primitive polynomial 0x11D, generator element α = 2).
	/// </summary>
	internal static byte[] ReedSolomonRemainder(byte[] data, int ecLength)
	{
		// Divisor = ∏(x − α^i) for i in [0, ecLength).
		byte[] divisor = new byte[ecLength];
		divisor[ecLength - 1] = 1; // the monomial x^0
		int root = 1;
		for (int i = 0; i < ecLength; i++)
		{
			// Each pass multiplies by (x − root): multiply every coefficient,
			// then add the not-yet-updated next element (the x·prefix term).
			for (int j = 0; j < ecLength; j++)
			{
				divisor[j] = (byte)Multiply(divisor[j], root);
				if (j + 1 < ecLength)
				{
					divisor[j] ^= divisor[j + 1];
				}
			}

			root = Multiply(root, 0x02);
		}

		byte[] result = new byte[ecLength];
		foreach (byte b in data)
		{
			int factor = b ^ result[0];
			Array.Copy(result, 1, result, 0, ecLength - 1);
			result[ecLength - 1] = 0;
			for (int j = 0; j < ecLength; j++)
			{
				result[j] ^= (byte)Multiply(divisor[j], factor);
			}
		}

		return result;
	}

	/// <summary>
	/// Interleaves equal-sized blocks: data codewords round-robin, then EC
	/// codewords round-robin (the standard codeword stream layout).
	/// </summary>
	internal static byte[] Interleave(byte[][] dataBlocks, byte[][] ecBlocks)
	{
		int dataLength = dataBlocks[0].Length;
		int ecLength = ecBlocks[0].Length;
		var result = new byte[dataBlocks.Length * (dataLength + ecLength)];
		int position = 0;
		for (int i = 0; i < dataLength; i++)
		{
			foreach (byte[] block in dataBlocks)
			{
				result[position++] = block[i];
			}
		}

		for (int i = 0; i < ecLength; i++)
		{
			foreach (byte[] block in ecBlocks)
			{
				result[position++] = block[i];
			}
		}

		return result;
	}

	/// <summary>The mask inversion predicate for each of the eight data masks.</summary>
	/// <exception cref="ArgumentOutOfRangeException">mask outside 0..7.</exception>
	internal static bool MaskBit(int mask, int row, int col) => mask switch
	{
		0 => (col + row) % 2 == 0,
		1 => row % 2 == 0,
		2 => col % 3 == 0,
		3 => (col + row) % 3 == 0,
		4 => (col / 3 + row / 2) % 2 == 0,
		5 => col * row % 2 + col * row % 3 == 0,
		6 => (col * row % 2 + col * row % 3) % 2 == 0,
		7 => ((col + row) % 2 + col * row % 3) % 2 == 0,
		_ => throw new ArgumentOutOfRangeException(nameof(mask), mask, "mask must be 0..7"),
	};

	/// <summary>Applies mask <paramref name="mask"/> to every non-function module.</summary>
	internal static void ApplyMask(bool[,] modules, bool[,] functions, int size, int mask)
	{
		for (int row = 0; row < size; row++)
		{
			for (int col = 0; col < size; col++)
			{
				if (!functions[row, col] && MaskBit(mask, row, col))
				{
					modules[row, col] ^= true;
				}
			}
		}
	}

	/// <summary>Draws both copies of the 15-bit format info (BCH generator 0x537, mask 0x5412).</summary>
	internal static void DrawFormatBits(bool[,] modules, int size, int mask)
	{
		int data = mask; // ECC level M format bits are 00 (L=01, M=00, Q=11, H=10)
		int rem = data;
		for (int i = 0; i < 10; i++)
		{
			rem = (rem << 1) ^ ((rem >> 9) * 0x537);
		}

		int bits = ((data << 10) | rem) ^ 0x5412;

		for (int i = 0; i <= 5; i++)
		{
			modules[i, 8] = Bit(bits, i);
		}

		modules[7, 8] = Bit(bits, 6);
		modules[8, 8] = Bit(bits, 7);
		modules[8, 7] = Bit(bits, 8);
		for (int i = 9; i < 15; i++)
		{
			modules[8, 14 - i] = Bit(bits, i);
		}

		for (int i = 0; i < 8; i++)
		{
			modules[8, size - 1 - i] = Bit(bits, i);
		}

		for (int i = 8; i < 15; i++)
		{
			modules[size - 15 + i, 8] = Bit(bits, i);
		}
	}

	/// <summary>Total mask penalty (ISO 18004 rules 1-4); lower is better.</summary>
	internal static long PenaltyScore(bool[,] matrix) =>
		PenaltyRule1(matrix) + PenaltyRule2(matrix) + PenaltyRule3(matrix) + PenaltyRule4(matrix);

	/// <summary>Rule 1: 3 + (run − 5) for every same-color run of 5+ modules in a row or column.</summary>
	internal static long PenaltyRule1(bool[,] matrix)
	{
		int size = matrix.GetLength(0);
		long score = 0;
		for (int row = 0; row < size; row++)
		{
			int run = 1;
			for (int col = 1; col < size; col++)
			{
				if (matrix[row, col] == matrix[row, col - 1])
				{
					run++;
				}
				else
				{
					score += RunPenalty(run);
					run = 1;
				}
			}

			score += RunPenalty(run);
		}

		for (int col = 0; col < size; col++)
		{
			int run = 1;
			for (int row = 1; row < size; row++)
			{
				if (matrix[row, col] == matrix[row - 1, col])
				{
					run++;
				}
				else
				{
					score += RunPenalty(run);
					run = 1;
				}
			}

			score += RunPenalty(run);
		}

		return score;
	}

	/// <summary>Rule 2: 3 for every 2×2 window whose modules share one color.</summary>
	internal static long PenaltyRule2(bool[,] matrix)
	{
		int size = matrix.GetLength(0);
		long score = 0;
		for (int row = 0; row < size - 1; row++)
		{
			for (int col = 0; col < size - 1; col++)
			{
				bool color = matrix[row, col];
				if (color == matrix[row, col + 1] && color == matrix[row + 1, col] && color == matrix[row + 1, col + 1])
				{
					score += 3;
				}
			}
		}

		return score;
	}

	/// <summary>
	/// Rule 3: 40 for every in-bounds 11-module window shaped 1011101 with four
	/// light modules on either side (horizontal or vertical), in either order.
	/// </summary>
	internal static long PenaltyRule3(bool[,] matrix)
	{
		int size = matrix.GetLength(0);
		long score = 0;
		for (int row = 0; row < size; row++)
		{
			for (int start = 0; start + 11 <= size; start++)
			{
				if (FinderLike(offset => matrix[row, start + offset]))
				{
					score += 40;
				}
			}
		}

		for (int col = 0; col < size; col++)
		{
			for (int start = 0; start + 11 <= size; start++)
			{
				if (FinderLike(offset => matrix[start + offset, col]))
				{
					score += 40;
				}
			}
		}

		return score;
	}

	/// <summary>Rule 4: 10 × floor(|dark% − 50| / 5).</summary>
	internal static long PenaltyRule4(bool[,] matrix)
	{
		int size = matrix.GetLength(0);
		int dark = 0;
		for (int row = 0; row < size; row++)
		{
			for (int col = 0; col < size; col++)
			{
				if (matrix[row, col])
				{
					dark++;
				}
			}
		}

		int percent = dark * 100 / (size * size);
		return Math.Abs(percent - 50) / 5 * 10;
	}

	private static long RunPenalty(int run) => run >= 5 ? 3 + (run - 5) : 0;

	// The finder-core sequence 1011101 with four light modules after it, and
	// the same with the light run first.
	private static readonly bool[] PatternCoreFirst = { true, false, true, true, true, false, true, false, false, false, false };
	private static readonly bool[] PatternLightFirst = { false, false, false, false, true, false, true, true, true, false, true };

	private static bool FinderLike(Func<int, bool> get) => Matches(get, PatternCoreFirst) || Matches(get, PatternLightFirst);

	private static bool Matches(Func<int, bool> get, bool[] pattern)
	{
		for (int i = 0; i < 11; i++)
		{
			if (get(i) != pattern[i])
			{
				return false;
			}
		}

		return true;
	}

	private static void AppendBits(List<bool> bits, int value, int length)
	{
		for (int i = length - 1; i >= 0; i--)
		{
			bits.Add((value >> i & 1) != 0);
		}
	}

	private static byte[][] SplitEqual(byte[] data, int blocks)
	{
		int size = data.Length / blocks;
		byte[][] result = new byte[blocks][];
		for (int i = 0; i < blocks; i++)
		{
			result[i] = data[(i * size)..((i + 1) * size)];
		}

		return result;
	}

	private static int Multiply(int x, int y)
	{
		int z = 0;
		for (int i = 7; i >= 0; i--)
		{
			z = (z << 1) ^ ((z >> 7) * 0x11D);
			z ^= (y >> i & 1) * x;
		}

		return z;
	}

	private static bool Bit(int value, int index) => (value >> index & 1) != 0;

	private static void DrawFinders(bool[,] modules, bool[,] functions, int size)
	{
		DrawFinder(3, 3);
		DrawFinder(size - 4, 3);
		DrawFinder(3, size - 4);

		void DrawFinder(int centerCol, int centerRow)
		{
			for (int dy = -4; dy <= 4; dy++)
			{
				for (int dx = -4; dx <= 4; dx++)
				{
					int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
					int col = centerCol + dx;
					int row = centerRow + dy;
					if (col >= 0 && col < size && row >= 0 && row < size)
					{
						// dark at distances 0,1 (core) and 3 (outer ring); light at 2 (gap) and 4 (separator)
						modules[row, col] = dist != 2 && dist != 4;
						functions[row, col] = true;
					}
				}
			}
		}
	}

	private static void DrawTiming(bool[,] modules, bool[,] functions, int size)
	{
		for (int i = 0; i < size; i++)
		{
			if (!functions[i, 6])
			{
				modules[i, 6] = i % 2 == 0;
				functions[i, 6] = true;
			}

			if (!functions[6, i])
			{
				modules[6, i] = i % 2 == 0;
				functions[6, i] = true;
			}
		}
	}

	private static void DrawAlignment(bool[,] modules, bool[,] functions, int size)
	{
		int center = size - 7;
		for (int dy = -2; dy <= 2; dy++)
		{
			for (int dx = -2; dx <= 2; dx++)
			{
				int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
				modules[center + dy, center + dx] = dist != 1;
				functions[center + dy, center + dx] = true;
			}
		}
	}

	private static void ReserveFormat(bool[,] functions, int size)
	{
		for (int i = 0; i <= 5; i++)
		{
			functions[i, 8] = true;
		}

		functions[7, 8] = true;
		functions[8, 8] = true;
		functions[8, 7] = true;
		for (int i = 9; i < 15; i++)
		{
			functions[8, 14 - i] = true;
		}

		for (int i = 0; i < 8; i++)
		{
			functions[8, size - 1 - i] = true;
		}

		for (int i = 8; i < 15; i++)
		{
			functions[size - 15 + i, 8] = true;
		}
	}

	private static void PlaceCodewords(bool[,] modules, bool[,] functions, int size, byte[] codewords)
	{
		int bitIndex = 0;
		int totalBits = codewords.Length * 8;
		for (int right = size - 1; right >= 1; right -= 2)
		{
			if (right == 6)
			{
				right = 5; // skip the vertical timing column
			}

			for (int vert = 0; vert < size; vert++)
			{
				for (int j = 0; j < 2; j++)
				{
					int col = right - j;
					bool upward = (right + 1 & 2) == 0;
					int row = upward ? size - 1 - vert : vert;
					if (!functions[row, col] && bitIndex < totalBits)
					{
						modules[row, col] = (codewords[bitIndex >> 3] >> (7 - (bitIndex & 7)) & 1) != 0;
						bitIndex++;
					}
				}
			}
		}
	}
}
