using System.Text;
using Vapor.ControlPlane;
using Xunit;

// CA1814 (prefer jagged arrays): the encoder's module grid API is bool[,]
// (a genuine square matrix) and the oracle mirrors it 1:1.
#pragma warning disable CA1814

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// QrEncoder unit tests plus an independent decoder oracle. The test side
/// re-derives the symbol structure (format-info layout, function patterns,
/// zigzag order, block de-interleaving) with its own code and verifies every
/// encode through a real decode path: mask recovery from the format bits,
/// Reed-Solomon syndromes exactly zero via Horner evaluation over a
/// Russian-peasant GF(256) multiply (a different formulation than the
/// encoder's), and a byte-mode segment parse back to the original text. That
/// keeps the in-repo encoder honest without any external dependency.
/// </summary>
public sealed class QrEncoderTests
{
	// Mirror of the encoder's version table, derived from the published
	// version-1..6 ECC-M capacities (14/26/42/62/84/106 bytes).
	private static readonly (int Size, int DataCodewords, int Blocks, int EcPerBlock)[] Table =
	{
		(21, 16, 1, 10),
		(25, 28, 1, 16),
		(29, 44, 1, 26),
		(33, 64, 2, 18),
		(37, 86, 2, 24),
		(41, 108, 4, 16),
	};

	public static TheoryData<int, int> VersionBoundaries => new()
	{
		{ 1, 21 },
		{ 14, 21 },
		{ 15, 25 },
		{ 26, 25 },
		{ 27, 29 },
		{ 42, 29 },
		{ 43, 33 },
		{ 62, 33 },
		{ 63, 37 },
		{ 84, 37 },
		{ 85, 41 },
		{ 106, 41 },
	};

	[Theory]
	[MemberData(nameof(VersionBoundaries))]
	public void Encode_SelectsSmallestFittingVersion(int byteLength, int expectedSize)
	{
		bool[,] matrix = QrEncoder.Encode(new string('a', byteLength));

		Assert.Equal(expectedSize, matrix.GetLength(0));
		Assert.Equal(expectedSize, matrix.GetLength(1));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Encode_RejectsBlankText(string? text)
	{
		Assert.Throws<ArgumentException>(() => QrEncoder.Encode(text!));
	}

	[Fact]
	public void Encode_RejectsTextBeyondCapacity()
	{
		Assert.Throws<ArgumentException>(() => QrEncoder.Encode(new string('a', QrEncoder.MaxByteLength + 1)));
		// Oversize can also come from multi-byte UTF-8, not just long ASCII.
		Assert.Throws<ArgumentException>(() => QrEncoder.Encode(new string('蒸', 36))); // 108 bytes
	}

	public static TheoryData<string> RoundTripTexts => new()
	{
		"Hi",
		"https://s.team/q/1/Ab3dEf9hK2mN",
		"https://steamcommunity.com/mobilelogin?oauth_token=abc123&steamid=76561198",
		new string('a', 14),
		new string('b', 26),
		new string('c', 42),
		new string('d', 62),
		new string('e', 84),
		new string('f', 106),
		"蒸汽平台扫码登录 ✓",
	};

	[Theory]
	[MemberData(nameof(RoundTripTexts))]
	public void Encode_DecodesBackThroughIndependentOracle(string text)
	{
		bool[,] matrix = QrEncoder.Encode(text);

		Assert.Equal(text, Decode(matrix));
	}

	[Fact]
	public void Encode_IsDeterministic()
	{
		bool[,] first = QrEncoder.Encode("https://s.team/q/1/determinism");
		bool[,] second = QrEncoder.Encode("https://s.team/q/1/determinism");

		Assert.Equal(first, second); // bool[,] structural equality
	}

	[Fact]
	public void Encode_BuildsCanonicalFunctionPatterns()
	{
		bool[,] m = QrEncoder.Encode("Hi");
		int size = 21;

		// All three finder patterns: dark at Chebyshev distance 0/1/3 from the
		// 7×7 center, light at distance 2; separators (distance 4) light.
		AssertFinder(3, 3);
		AssertFinder(size - 4, 3);
		AssertFinder(3, size - 4);

		// Timing patterns alternate dark/light between the finders.
		for (int i = 8; i <= 12; i++)
		{
			Assert.Equal(i % 2 == 0, m[6, i]);
			Assert.Equal(i % 2 == 0, m[i, 6]);
		}

		// The always-dark module sits above the bottom-left finder.
		Assert.True(m[size - 8, 8]);

		void AssertFinder(int centerCol, int centerRow)
		{
			for (int dy = -4; dy <= 4; dy++)
			{
				for (int dx = -4; dx <= 4; dx++)
				{
					int col = centerCol + dx;
					int row = centerRow + dy;
					if (col is < 0 or >= 21 || row is < 0 or >= 21)
					{
						continue;
					}

					int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
					Assert.Equal(dist is 0 or 1 or 3, m[row, col]);
				}
			}
		}
	}

	[Fact]
	public void Encode_PlacesSingleAlignmentPatternFromVersion2()
	{
		bool[,] m = QrEncoder.Encode(new string('a', 20)); // version 2, 25×25
		int center = 25 - 7;

		for (int dy = -2; dy <= 2; dy++)
		{
			for (int dx = -2; dx <= 2; dx++)
			{
				int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
				Assert.Equal(dist != 1, m[center + dy, center + dx]);
			}
		}
	}

	[Fact]
	public void DrawFormatBits_BothCopiesAgree_LevelM_MasksDistinct()
	{
		var words = new HashSet<int>();
		for (int mask = 0; mask < 8; mask++)
		{
			var m = new bool[21, 21];
			QrEncoder.DrawFormatBits(m, 21, mask);

			int first = ReadFormat(m, 21, second: false);
			Assert.Equal(first, ReadFormat(m, 21, second: true));

			int bits = first ^ 0x5412;
			Assert.Equal(0, bits >> 13); // ECC level M format bits are 00
			Assert.Equal(mask, (bits >> 10) & 7); // mask occupies the low 3 data bits
			words.Add(bits);
		}

		Assert.Equal(8, words.Count);
	}

	[Fact]
	public void BuildDataCodewords_MatchesHandDerivedBitstream()
	{
		// "01234567" in byte mode: mode 0100, 8-bit length 00001000, ASCII
		// bytes 0x30..0x37, 4-bit terminator — nibbles 4,0,8,3,0,3,1,…,3,7,0 —
		// then the 0xEC/0x11 pad pair alternating to 16 codewords.
		byte[] result = QrEncoder.BuildDataCodewords("01234567"u8.ToArray(), 16);

		Assert.Equal(
			new byte[] { 0x40, 0x83, 0x03, 0x13, 0x23, 0x33, 0x43, 0x53, 0x63, 0x70, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11 },
			result);
	}

	[Fact]
	public void BuildDataCodewords_PadsShortPayloads()
	{
		// Single byte "A" (0x41): nibbles 4,0,1,4,1,0 → three codewords then pads.
		byte[] result = QrEncoder.BuildDataCodewords("A"u8.ToArray(), 16);

		Assert.Equal(0x40, result[0]);
		Assert.Equal(0x14, result[1]);
		Assert.Equal(0x10, result[2]);
		Assert.Equal(0xEC, result[3]);
		Assert.Equal(0x11, result[4]);
		Assert.Equal(0xEC, result[15]);
		Assert.Equal(16, result.Length);
	}

	[Theory]
	[InlineData(10)]
	[InlineData(16)]
	[InlineData(24)]
	[InlineData(26)]
	public void ReedSolomonRemainder_SyndromesVanish(int ecLength)
	{
		byte[] data = Enumerable.Range(1, 20).Select(i => (byte)(i * 7 % 251)).ToArray();

		byte[] ec = QrEncoder.ReedSolomonRemainder(data, ecLength);

		Assert.Equal(ecLength, ec.Length);
		byte[] codeword = [.. data, .. ec];
		int alphaPower = 1;
		for (int i = 0; i < ecLength; i++)
		{
			int value = 0;
			foreach (byte b in codeword)
			{
				value = GfMultiply(value, alphaPower) ^ b;
			}

			Assert.Equal(0, value);
			alphaPower = GfMultiply(alphaPower, 2);
		}
	}

	[Fact]
	public void Interleave_RoundRobinsDataThenEc()
	{
		byte[] result = QrEncoder.Interleave([[1, 2], [3, 4]], [[5, 6], [7, 8]]);

		Assert.Equal(new byte[] { 1, 3, 2, 4, 5, 7, 6, 8 }, result);
	}

	public static TheoryData<int, int, int, bool> MaskBits => new()
	{
		{ 0, 0, 0, true },
		{ 0, 4, 5, false },
		{ 0, 4, 4, true },
		{ 1, 2, 9, true },
		{ 1, 3, 9, false },
		{ 2, 7, 0, true },
		{ 2, 7, 5, false },
		{ 3, 1, 2, true },
		{ 3, 1, 4, false },
		{ 4, 4, 7, true },
		{ 4, 3, 7, false },
		{ 5, 0, 5, true },
		{ 5, 1, 5, false },
		{ 5, 2, 3, true },
		{ 6, 0, 5, true },
		{ 6, 1, 5, false },
		{ 6, 1, 2, true },
		{ 7, 0, 0, true },
		{ 7, 1, 0, false },
		{ 7, 3, 3, true },
		{ 7, 2, 2, false },
	};

	[Theory]
	[MemberData(nameof(MaskBits))]
	public void MaskBit_MatchesSpecFormulas(int mask, int row, int col, bool expected)
	{
		Assert.Equal(expected, QrEncoder.MaskBit(mask, row, col));
	}

	[Theory]
	[InlineData(8)]
	[InlineData(-1)]
	public void MaskBit_RejectsOutOfRangeMask(int mask)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => QrEncoder.MaskBit(mask, 3, 4));
	}

	[Fact]
	public void ApplyMask_FlipsOnlyNonFunctionModules()
	{
		var modules = new bool[21, 21];
		var functions = new bool[21, 21];
		for (int row = 0; row <= 8; row++)
		{
			for (int col = 0; col <= 8; col++)
			{
				functions[row, col] = true;
			}
		}

		modules[4, 4] = true; // function module with a set value

		QrEncoder.ApplyMask(modules, functions, 21, 0);

		Assert.True(modules[4, 4]); // function space untouched
		Assert.True(modules[9, 9]); // non-function, (9+9) even → flipped by mask 0
		Assert.False(modules[9, 10]); // non-function, odd → mask 0 leaves light
	}

	[Fact]
	public void PenaltyRule1_ChargesLongRunsInRowsAndColumns()
	{
		var allLight = new bool[21, 21];

		// 42 lines × (3 + (21 − 5)).
		Assert.Equal(42 * 19, QrEncoder.PenaltyRule1(allLight));

		var checkerboard = new bool[21, 21];
		for (int row = 0; row < 21; row++)
		{
			for (int col = 0; col < 21; col++)
			{
				checkerboard[row, col] = (row + col) % 2 == 0;
			}
		}

		Assert.Equal(0, QrEncoder.PenaltyRule1(checkerboard));
	}

	[Fact]
	public void PenaltyRule2_ChargesUniformTwoByTwoWindows()
	{
		var allLight = new bool[21, 21];
		Assert.Equal(400 * 3, QrEncoder.PenaltyRule2(allLight));

		// One dark 2×2 at the corner turns exactly three windows non-uniform.
		var withBlock = new bool[21, 21];
		withBlock[0, 0] = withBlock[0, 1] = withBlock[1, 0] = withBlock[1, 1] = true;
		Assert.Equal(397 * 3, QrEncoder.PenaltyRule2(withBlock));
	}

	[Fact]
	public void PenaltyRule3_ChargesFinderLikeSequencesInAllOrientations()
	{
		Assert.Equal(0, QrEncoder.PenaltyRule3(new bool[21, 21]));

		// Each 11-module pattern embeds its own mirror (core 1011101 + trailing
		// 4 light is also 4 light + mirrored core when shifted by 4), so the
		// placements below push every unintended second window out of bounds:
		// core-first flush left (its mirror would start at −4), light-first at
		// offset 7 (its embedded mirror at +4 would end at 21).
		var coreFirst = new bool[21, 21];
		SetRow(coreFirst, 10, 0, [true, false, true, true, true, false, true, false, false, false, false]);
		Assert.Equal(40, QrEncoder.PenaltyRule3(coreFirst));

		var lightFirst = new bool[21, 21];
		SetRow(lightFirst, 10, 7, [false, false, false, false, true, false, true, true, true, false, true]);
		Assert.Equal(40, QrEncoder.PenaltyRule3(lightFirst));

		var vertical = new bool[21, 21];
		SetColumn(vertical, 10, 0, [true, false, true, true, true, false, true, false, false, false, false]);
		Assert.Equal(40, QrEncoder.PenaltyRule3(vertical));

		var both = new bool[21, 21];
		SetRow(both, 5, 0, [true, false, true, true, true, false, true, false, false, false, false]);
		SetColumn(both, 15, 7, [false, false, false, false, true, false, true, true, true, false, true]);
		Assert.Equal(80, QrEncoder.PenaltyRule3(both));
	}

	[Fact]
	public void PenaltyRule4_ChargesDarkRatioDeviation()
	{
		var allLight = new bool[21, 21];
		Assert.Equal(100, QrEncoder.PenaltyRule4(allLight)); // |0 − 50|/5 × 10

		var checkerboard = new bool[21, 21];
		for (int row = 0; row < 21; row++)
		{
			for (int col = 0; col < 21; col++)
			{
				checkerboard[row, col] = (row + col) % 2 == 0;
			}
		}

		Assert.Equal(0, QrEncoder.PenaltyRule4(checkerboard)); // 221/441 rounds to 50%
	}

	[Fact]
	public void PenaltyScore_IsTheSumOfTheFourRules()
	{
		bool[,] m = QrEncoder.Encode("https://s.team/q/1/penalty");

		Assert.Equal(
			QrEncoder.PenaltyRule1(m) + QrEncoder.PenaltyRule2(m) + QrEncoder.PenaltyRule3(m) + QrEncoder.PenaltyRule4(m),
			QrEncoder.PenaltyScore(m));
	}

	[Fact]
	public void ToSvg_RendersQuietZoneDimensionsAndModulePath()
	{
		bool[,] m = QrEncoder.Encode("Hi");
		string svg = QrEncoder.ToSvg(m);

		Assert.StartsWith("<svg ", svg, StringComparison.Ordinal);
		Assert.EndsWith("</svg>", svg, StringComparison.Ordinal);
		Assert.Contains("viewBox=\"0 0 29 29\"", svg, StringComparison.Ordinal); // 21 + 2×4 quiet zone
		Assert.Contains("width=\"116\"", svg, StringComparison.Ordinal); // 29 × scale 4
		Assert.Contains("height=\"116\"", svg, StringComparison.Ordinal);
		Assert.Contains("fill=\"#ffffff\"", svg, StringComparison.Ordinal);
		Assert.Contains("shape-rendering=\"crispEdges\"", svg, StringComparison.Ordinal);

		int dark = 0;
		foreach (bool module in m)
		{
			if (module)
			{
				dark++;
			}
		}

		int pathStart = svg.IndexOf("<path d=\"", StringComparison.Ordinal) + "<path d=\"".Length;
		int pathEnd = svg.IndexOf('"', pathStart);
		string path = svg[pathStart..pathEnd];
		Assert.Equal(dark, path.Count(ch => ch == 'M'));
	}

	[Fact]
	public void ToSvg_RespectsScaleAndBorder()
	{
		string svg = QrEncoder.ToSvg(QrEncoder.Encode("Hi"), scale: 8, border: 2);

		Assert.Contains("viewBox=\"0 0 25 25\"", svg, StringComparison.Ordinal); // 21 + 2×2
		Assert.Contains("width=\"200\"", svg, StringComparison.Ordinal); // 25 × 8
	}

	private static void SetRow(bool[,] m, int row, int start, bool[] values)
	{
		for (int i = 0; i < values.Length; i++)
		{
			m[row, start + i] = values[i];
		}
	}

	private static void SetColumn(bool[,] m, int col, int start, bool[] values)
	{
		for (int i = 0; i < values.Length; i++)
		{
			m[start + i, col] = values[i];
		}
	}

	// ---- independent decoder oracle ----

	private static string Decode(bool[,] m)
	{
		int size = m.GetLength(0);
		int index = (size - 21) / 4;
		(_, int dataCodewords, int blocks, int ecPerBlock) = Table[index];

		int raw = ReadFormat(m, size, second: false);
		Assert.Equal(raw, ReadFormat(m, size, second: true));

		int bits = raw ^ 0x5412;
		int data = bits >> 10;
		int rem = data;
		for (int i = 0; i < 10; i++)
		{
			rem = (rem << 1) ^ ((rem >> 9) * 0x537);
		}

		Assert.Equal((data << 10) | rem, bits); // BCH recomputation lands on the same word
		Assert.Equal(0, data >> 2); // ECC level M
		int mask = data & 7; // the mask id rides the low 3 data bits

		bool[,] functions = BuildFunctionMap(size, index);

		var unmasked = (bool[,])m.Clone();
		for (int row = 0; row < size; row++)
		{
			for (int col = 0; col < size; col++)
			{
				if (!functions[row, col] && DecoderMaskBit(mask, row, col))
				{
					unmasked[row, col] ^= true;
				}
			}
		}

		int totalBits = (dataCodewords + blocks * ecPerBlock) * 8;
		var codewords = new List<byte>();
		int seenBits = 0;
		int current = 0;
		for (int right = size - 1; right >= 1; right -= 2)
		{
			if (right == 6)
			{
				right = 5;
			}

			for (int vert = 0; vert < size; vert++)
			{
				for (int j = 0; j < 2; j++)
				{
					int col = right - j;
					bool upward = (right + 1 & 2) == 0;
					int row = upward ? size - 1 - vert : vert;
					if (!functions[row, col] && seenBits < totalBits)
					{
						current = (current << 1) | (unmasked[row, col] ? 1 : 0);
						if (++seenBits % 8 == 0)
						{
							codewords.Add((byte)current);
							current = 0;
						}
					}
				}
			}
		}

		Assert.Equal(totalBits, seenBits);

		int dataLength = dataCodewords / blocks;
		var dataBlocks = new byte[blocks][];
		var ecBlocks = new byte[blocks][];
		for (int b = 0; b < blocks; b++)
		{
			dataBlocks[b] = new byte[dataLength];
			ecBlocks[b] = new byte[ecPerBlock];
		}

		int position = 0;
		for (int i = 0; i < dataLength; i++)
		{
			for (int b = 0; b < blocks; b++)
			{
				dataBlocks[b][i] = codewords[position++];
			}
		}

		for (int i = 0; i < ecPerBlock; i++)
		{
			for (int b = 0; b < blocks; b++)
			{
				ecBlocks[b][i] = codewords[position++];
			}
		}

		for (int b = 0; b < blocks; b++)
		{
			byte[] codeword = [.. dataBlocks[b], .. ecBlocks[b]];
			int alphaPower = 1;
			for (int i = 0; i < ecPerBlock; i++)
			{
				int value = 0;
				foreach (byte c in codeword)
				{
					value = GfMultiply(value, alphaPower) ^ c;
				}

				Assert.Equal(0, value); // Reed-Solomon syndrome must vanish
				alphaPower = GfMultiply(alphaPower, 2);
			}
		}

		var segmentBits = new List<bool>();
		foreach (byte[] block in dataBlocks)
		{
			foreach (byte b in block)
			{
				for (int bit = 7; bit >= 0; bit--)
				{
					segmentBits.Add((b >> bit & 1) != 0);
				}
			}
		}

		int Segment(int offset, int length)
		{
			int value = 0;
			for (int i = 0; i < length; i++)
			{
				if (segmentBits[offset + i])
				{
					value |= 1 << (length - 1 - i);
				}
			}

			return value;
		}

		Assert.Equal(0b0100, Segment(0, 4)); // byte mode
		int length = Segment(4, 8);
		var payload = new byte[length];
		for (int i = 0; i < length; i++)
		{
			payload[i] = (byte)Segment(12 + i * 8, 8);
		}

		for (int i = 12 + length * 8; i < Math.Min(12 + length * 8 + 4, segmentBits.Count); i++)
		{
			Assert.False(segmentBits[i]); // terminator bits are zero
		}

		return Encoding.UTF8.GetString(payload);
	}

	private static int ReadFormat(bool[,] m, int size, bool second)
	{
		int value = 0;
		for (int i = 0; i < 15; i++)
		{
			(int row, int col) = FormatPosition(i, size, second);
			if (m[row, col])
			{
				value |= 1 << i;
			}
		}

		return value;
	}

	private static (int Row, int Col) FormatPosition(int i, int size, bool second)
	{
		if (!second)
		{
			return i switch
			{
				<= 5 => (i, 8),
				6 => (7, 8),
				7 => (8, 8),
				8 => (8, 7),
				_ => (8, 14 - i),
			};
		}

		return i < 8 ? (8, size - 1 - i) : (size - 15 + i, 8);
	}

	private static bool[,] BuildFunctionMap(int size, int index)
	{
		var functions = new bool[size, size];
		foreach ((int row0, int col0) in new[] { (0, 0), (0, size - 8), (size - 8, 0) })
		{
			for (int r = 0; r < 8; r++)
			{
				for (int c = 0; c < 8; c++)
				{
					functions[row0 + r, col0 + c] = true;
				}
			}
		}

		for (int i = 0; i < size; i++)
		{
			functions[i, 6] = true;
			functions[6, i] = true;
		}

		if (index >= 1)
		{
			int center = size - 7;
			for (int dy = -2; dy <= 2; dy++)
			{
				for (int dx = -2; dx <= 2; dx++)
				{
					functions[center + dy, center + dx] = true;
				}
			}
		}

		for (int i = 0; i < 15; i++)
		{
			(int r1, int c1) = FormatPosition(i, size, second: false);
			(int r2, int c2) = FormatPosition(i, size, second: true);
			functions[r1, c1] = true;
			functions[r2, c2] = true;
		}

		functions[size - 8, 8] = true; // dark module
		return functions;
	}

	// The eight standard data-mask predicates, written from the spec here so a
	// formula typo in the encoder cannot hide behind an identical test copy.
	private static bool DecoderMaskBit(int mask, int row, int col) => mask switch
	{
		0 => (col + row) % 2 == 0,
		1 => row % 2 == 0,
		2 => col % 3 == 0,
		3 => (col + row) % 3 == 0,
		4 => (col / 3 + row / 2) % 2 == 0,
		5 => col * row % 2 + col * row % 3 == 0,
		6 => (col * row % 2 + col * row % 3) % 2 == 0,
		7 => ((col + row) % 2 + col * row % 3) % 2 == 0,
		_ => throw new InvalidOperationException("unreachable in the oracle"),
	};

	// Russian-peasant GF(256) multiply (primitive polynomial 0x11D) — a
	// different formulation than the encoder's fixed-iteration loop.
	private static int GfMultiply(int x, int y)
	{
		int z = 0;
		while (y != 0)
		{
			if ((y & 1) != 0)
			{
				z ^= x;
			}

			x <<= 1;
			if ((x & 0x100) != 0)
			{
				x ^= 0x11D;
			}

			y >>= 1;
		}

		return z;
	}
}
