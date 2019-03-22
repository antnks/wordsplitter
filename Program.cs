using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace wordsplitter
{
	class Program
	{
		static async Task Main(string[] args)
		{
			string[] lines = File.ReadAllLines(args[0]);
			Console.WriteLine($"Loaded {lines.Length:n0} words");

			ConcurrentBag<string> stage1 = new ConcurrentBag<string>(lines);
			ConcurrentBag<string> stage2 = new ConcurrentBag<string>();
			ConcurrentDictionary<string, string> words = new ConcurrentDictionary<string, string>();

			await RunTasks(stage1, () => Pass1 (stage1, stage2, words));

			int capCount = words.Count;
			Console.WriteLine($"Found {capCount:n0} words starting with capital");

			ConcurrentBag<string> stage3 = new ConcurrentBag<string>();
			await RunTasks(stage2, () => Pass2 (stage2, stage3, words));

			Console.WriteLine($"Found {words.Count - capCount} repeating words");

			ICollection<string> stageWords = words.Values;
			ConcurrentBag<string> input = stage3;
			List<string> candidates = new List<string>();

			do
			{
				int startingCount = input.Count;
				DateTime startingTime = DateTime.Now;
				Console.WriteLine($"Starting searching by dictionary. {input.Count:n0} input words. " +
													   $"{stageWords.Count:n0} dictionary words to check.");

				ConcurrentDictionary<string, string> newCandidates = new ConcurrentDictionary<string, string>();
				ConcurrentBag<string> nextStage = new ConcurrentBag<string>();

				Task completedTask;
				Task updateGuiTask;
				Task workerTask = RunTasks(input, () => WorkerTasks(input, nextStage, newCandidates, stageWords));

				do
				{
					// ReSharper disable once MethodSupportsCancellation
					updateGuiTask = Task.Delay(1000).ContinueWith (t => ProgressBar(startingTime, input.Count, startingCount, newCandidates.Count));
					completedTask = await Task.WhenAny(workerTask, updateGuiTask).ConfigureAwait (false);
				}
				while (completedTask != workerTask);

				// analysisTask should be completed already
				// but it is safer to wait for all :)
				await Task.WhenAll(workerTask, updateGuiTask).ConfigureAwait (false);

				stageWords = newCandidates.Values;
				candidates.AddRange(stageWords);
				input = nextStage;

			} while (stageWords.Any());

			List<string> output = words.Values.ToList();
			output.Sort();
			WriteToFile(args[1], output);

			candidates.Sort();
			WriteToFile(args[2], candidates);

			output = input.ToList();
			output.Sort(); 
			WriteToFile(args[3], output);
		}

		static void Pass1(ConcurrentBag<string> stage1, ConcurrentBag<string> stage2, ConcurrentDictionary<string, string> words)
		{
			string item;

			if (!stage1.TryTake(out item))
				return;

				bool wasFound = false;
				int  len      = 1;
				for (int i = item.Length - 1; i >= 0; --i, ++len)
					if (char.IsUpper(item, i) && (i - 1 < 0 || !char.IsUpper(item, i - 1)))
					{
						string word = item.Substring(i, len).ToLowerInvariant();

						len      = 0;
						wasFound = true;
						words.TryAdd(word, word); // ignore return, returns false if already added
					}

				if (!wasFound)
					stage2.Add(item);
				else if (len != 1)
					throw new Exception("first letter is not capital?");
		}

		static void Pass2(ConcurrentBag<string> stage2, ConcurrentBag<string> stage3, ConcurrentDictionary<string, string> words)
		{
			string item;

			if (!stage2.TryTake(out item))
				return;

			if (item.Length % 2 != 0)
				return;

			int len = item.Length / 2;
			for (int i = 0; i < len; ++i)
			{
				if (item[i] == item[i + len])
					continue;

				stage3.Add(item);
				return;
			}

			string word = item.Substring(0, len);
			words.TryAdd(word, word); // ignore return, returns false if already added
		}

		static void WorkerTasks(ConcurrentBag<string> input, ConcurrentBag<string> nextStage, ConcurrentDictionary<string, string> newCandidates, ICollection<string> stageWords)
		{
			string item;

			if (!input.TryTake(out item))
				return;

			bool wasFound = false;
			foreach (string word in stageWords)
			{
				if (!item.StartsWith(word))
					continue;

				wasFound = true;
				string newCandidate = item.Substring(word.Length);
				if (newCandidate.Length != 0)
					newCandidates.TryAdd(newCandidate, newCandidate);

				break;
			}

			if (!wasFound)
				nextStage.Add(item);
		}

		static void WriteToFile(string path, IEnumerable<string> data)
		{
			File.WriteAllLines(path, data);
		}

		static async Task RunTasks(ConcurrentBag<string> input, Action action)
		{
			List<Task> tasks = new List<Task>();
			while (!input.IsEmpty)
			{
				Task task = Task.Run(action);

				tasks.Add(task);
				await BalanceLoad(tasks);
			}
			await Task.WhenAll(tasks);
		}

		static async Task BalanceLoad(List<Task> tasks)
		{
			if (tasks.Count == 8)
			{
				Task completedTask = await Task.WhenAny(tasks);
				tasks.Remove(completedTask);
				foreach (Task t in tasks.ToArray())
					if (t.IsCompleted)
						tasks.Remove(t);
			}
		}

		static void ProgressBar(DateTime startingTime, int inputCount, int startingCount, int newCandidatesCount)
		{
			DateTime now       = DateTime.Now;
			TimeSpan elapsed   = now - startingTime;
			int leftItems = inputCount;
			int doneItems = startingCount - leftItems;
			int speed = (int)(doneItems / elapsed.TotalSeconds);
			if (speed != 0)
			{
				TimeSpan remaining = TimeSpan.FromSeconds(leftItems / speed);
				Console.WriteLine($"{now}: {leftItems:n0} input words left. " +
				$"{newCandidatesCount:n0} new candidates discovered. ETA {remaining}");
			}
		}
	}
}
