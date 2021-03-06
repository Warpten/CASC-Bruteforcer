﻿using CASCBruteforcer.Algorithms;
using CASCBruteforcer.Helpers;
using Cloo;
using OpenCLlib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace CASCBruteforcer.Bruteforcers
{
	class Jenkins96 : IHash
	{
		const string LISTFILE_URL = "https://bnet.marlam.in/listfile.php?unk=1";
		const long GLOBAL_WORKSIZE = uint.MaxValue; // sizeof(size_t) usually uint

		private ComputeDeviceTypes ComputeDevice;
		private string[] Masks;
		private HashSet<ulong> TargetHashes;
		private bool IsBenchmark = false;
		private bool IsMirrored = false;

		private Queue<ulong> ResultQueue;
		private HashSet<string> ResultStrings;

		public void LoadParameters(params string[] args)
		{
			if (args.Length < 3)
				throw new ArgumentException("Incorrect number of arguments");

			// what device to use
			switch (args[1].ToLower().Trim())
			{
				case "gpu":
					ComputeDevice = ComputeDeviceTypes.Gpu;
					break;
				case "cpu":
					ComputeDevice = ComputeDeviceTypes.Cpu;
					break;
				default:
					ComputeDevice = ComputeDeviceTypes.All;
					break;
			}

			// format + validate template masks
			if (File.Exists(args[2]))
			{
				Masks = File.ReadAllLines(args[2]).Where(x => x.Contains('%')).Select(x => Normalise(x)).ToArray();
			}
			else if (args[2].Contains('%'))
			{
				Masks = new string[] { Normalise(args[2]) };
			}

			if (Masks == null || Masks.Length == 0)
				throw new ArgumentException("No valid masks");

			// check for mirrored flag
			IsMirrored = (args.Length > 3 && args[3].Trim() == "1");

			// check for listfile and sort out the target hashes
			ParseHashes();

			if (TargetHashes.Count <= 1)
				throw new ArgumentException("Unknown listfile is missing or empty");

			ResultQueue = new Queue<ulong>();
			ResultStrings = new HashSet<string>();
			IsBenchmark = false;
		}

		public void LoadTestParameters(string device, string mask)
		{
			// what device to use
			switch (device)
			{
				case "gpu":
					ComputeDevice = ComputeDeviceTypes.Gpu;
					break;
				case "cpu":
					ComputeDevice = ComputeDeviceTypes.Cpu;
					break;
				default:
					ComputeDevice = ComputeDeviceTypes.All;
					break;
			}

			Masks = new string[] { Normalise(mask) };
			TargetHashes = new HashSet<ulong>() { 0, 4097458660625243137 };

			ResultQueue = new Queue<ulong>();
			ResultStrings = new HashSet<string>();
			IsBenchmark = true;
		}


		public void Start()
		{
			for (int i = 0; i < Masks.Length; i++)
				Run(i);

			LogAndExport();
		}

		private void Run(int m)
		{
			string mask = Masks[m];

			// resize mask to next % 12 for faster jenkins
			byte[] maskdata = Encoding.ASCII.GetBytes(mask);
			Array.Resize(ref maskdata, (mask.Length + (12 - mask.Length % 12) % 12));

			// calculate the indicies of the wildcard chars
			byte[] maskoffsets = Enumerable.Range(0, mask.Length).Where(i => mask[i] == '%').Select(i => (byte)i).ToArray();
			if (maskoffsets.Length > 12 * (IsMirrored ? 2 : 1))
			{
				Console.WriteLine($"Error: Too many wildcards - maximum is {12 * (IsMirrored ? 2 : 1)}. `{mask}`");
				return;
			}

			// mirrored is two indentical masks so must have an even count of wildcards
			if (IsMirrored && maskoffsets.Length % 2 != 0)
			{
				Console.WriteLine($"Error: Mirrored flag used with an odd number of wildcards. `{mask}`");
				return;
			}

			// reorder mirrored indices for faster permutation computing
			if (IsMirrored)
			{
				int halfcount = maskoffsets.Length / 2;
				byte[] temp = new byte[maskoffsets.Length];
				for (int i = 0; i < halfcount; i++)
				{
					temp[i * 2] = maskoffsets[i];
					temp[(i * 2) + 1] = maskoffsets[halfcount + i];
				}
				maskoffsets = temp;
			}

			// replace kernel placeholders - faster than using buffers
			KernelWriter kernel = new KernelWriter(Properties.Resources.Jenkins);
			kernel.ReplaceArray("DATA", maskdata);
			kernel.ReplaceArray("OFFSETS", maskoffsets);
			kernel.ReplaceArray("HASHES", TargetHashes);
			kernel.Replace("DATA_SIZE_REAL", mask.Length);
			kernel.ReplaceOffsetArray(TargetHashes);

			// load CL - filter contexts to the specific device type
			MultiCL cl = new MultiCL(ComputeDevice);
			Console.WriteLine($"Loading kernel - {TargetHashes.Count - 1} hashes. This may take a minute...");
			cl.SetKernel(kernel.ToString(), IsMirrored ? "BruteforceMirrored" : "Bruteforce");

			// limit workload to MAX_WORKSIZE and use an on-device loop to breach that value
			BigInteger combinations = BigInteger.Pow(39, maskoffsets.Length / (IsMirrored ? 2 : 1)); // total combinations
			uint loops = (uint)Math.Floor(Math.Exp(BigInteger.Log(combinations) - BigInteger.Log(GLOBAL_WORKSIZE)));

			// Start the work
			Console.WriteLine($"Starting Jenkins Hashing :: {combinations} combinations ");
			Stopwatch time = Stopwatch.StartNew();

			// output buffer arg
			var resultArg = CLArgument<ulong>.CreateReturn(TargetHashes.Count);

			// set up internal loop of GLOBAL_WORKSIZE
			if (loops > 0)
			{
				// loop size, index offset, output buffer
				cl.SetParameter(loops, (ulong)0, resultArg);
				Enqueue(cl.InvokeReturn<ulong>(GLOBAL_WORKSIZE, TargetHashes.Count));
				combinations -= loops * GLOBAL_WORKSIZE;
			}

			// process remaining
			if (combinations > 0)
			{
				// loop size, index offset, output buffer
				cl.SetParameter(1, (ulong)(loops * GLOBAL_WORKSIZE), resultArg);
				Enqueue(cl.InvokeReturn<ulong>((long)combinations, TargetHashes.Count));
			}

			time.Stop();
			Console.WriteLine($"Completed in {time.Elapsed.TotalSeconds.ToString("0.00")} secs");
			Validate(mask, maskoffsets);
		}


		#region Validation
		private void Enqueue(ulong[] results)
		{
			// dump everything into a collection and deal with it later
			foreach (var r in results)
				if (r != 0)
					ResultQueue.Enqueue(r);
		}

		private void Validate(string mask, byte[] maskoffsets)
		{
			char[] maskdata = mask.ToCharArray();

			// sanity check the results
			var j = new JenkinsHash();
			while (ResultQueue.Count > 0)
			{
				string s = StringGenerator.Generate(maskdata, ResultQueue.Dequeue(), maskoffsets, IsMirrored);
				ulong h = j.ComputeHash(s);
				if (TargetHashes.Contains(h))
					ResultStrings.Add(s);
			}
		}

		private void LogAndExport()
		{
			// log completion
			Console.WriteLine($"Found {ResultStrings.Count}:");

			if (ResultStrings.Count > 0)
			{
				// print to the screen
				foreach (var r in ResultStrings)
					Console.WriteLine($"  {r}");

				if (!IsBenchmark)
				{
					// write to Output.txt
					using (var sw = new StreamWriter(File.OpenWrite("Output.txt")))
					{
						sw.BaseStream.Position = sw.BaseStream.Length;
						foreach (var r in ResultStrings)
							sw.WriteLine(r);
					}
				}
			}

			Console.WriteLine("");
		}

		#endregion

		#region Unknown Hash Functions
		private void ParseHashes()
		{
			// get data
			string[] lines = new string[0];

			// re-download every 6 hours or if missing
			if (!File.Exists("unk_listfile.txt") || (DateTime.Now - File.GetLastWriteTime("unk_listfile.txt")).TotalHours >= 6)
				DownloadUnknownListFile("unk_listfile.txt");

			// check it actually exists
			if (File.Exists("unk_listfile.txt"))
				lines = File.ReadAllLines("unk_listfile.txt");

			// parse items - hex and standard because why not
			ulong dump = 0;
			IEnumerable<ulong> hashes = new ulong[1]; // 0 hash is used as a dump
#if DEBUG
			hashes = hashes.Concat(new ulong[] { 4097458660625243137, 13345699920692943597 }); // test hashes for the README examples
#endif
			hashes = hashes.Concat(lines.Where(x => ulong.TryParse(x.Trim(), NumberStyles.HexNumber, null, out dump)).Select(x => dump)); // hex
			hashes = hashes.Concat(lines.Where(x => ulong.TryParse(x.Trim(), out dump)).Select(x => dump)); // standard
			hashes = hashes.OrderBy(HashSort); // order by first byte - IMPORTANT

			TargetHashes = new HashSet<ulong>(hashes);
		}

		private void DownloadUnknownListFile(string name)
		{
			try
			{
				HttpWebRequest req = (HttpWebRequest)WebRequest.Create(LISTFILE_URL);
				using (WebResponse resp = req.GetResponse())
				using (FileStream fs = File.Create(name))
					resp.GetResponseStream().CopyTo(fs);

				req.Abort();
				Console.WriteLine("Downloaded unknown listfile");
			}
			catch
			{
				Console.WriteLine($"Unable to download unknown listfile from `{LISTFILE_URL}`");
			}
		}
		#endregion

		#region Helpers
		private string Normalise(string s) => s.Trim().Replace("/", "\\").ToUpperInvariant();

		public static Func<ulong, ulong> HashSort = (x) => x & 0xFF;
		#endregion
	}
}
