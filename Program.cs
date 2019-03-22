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
			var lines = File.ReadAllLines(args[0]);
			Console.WriteLine($"Loaded {lines.Length:n0} words");

			var stage1 = new ConcurrentBag<string>(lines);
			var stage2 = new ConcurrentBag<string>();
			var words = new ConcurrentDictionary<string, string>();

			await RunTasks(stage1, () =>
			{
				if (!stage1.TryTake(out var item))
					return;

					bool wasFound = false;
					int  len      = 1;
					for (int i = item.Length - 1; i >= 0; --i, ++len)
					{
						if (char.IsUpper(item, i) && (i - 1 < 0 || !char.IsUpper(item, i - 1)))
						{
							var word = item.Substring(i, len).ToLowerInvariant();

							len      = 0;
							wasFound = true;
							words.TryAdd(word, word); // ignore return, returns false if already added
						}
					}

					if (!wasFound)
						stage2.Add(item);
					else if (len != 1)
						throw new Exception("first letter is not capital?");
			});

			int capCount = words.Count;
			Console.WriteLine($"Found {capCount:n0} words starting with capital");

			var stage3 = new ConcurrentBag<string>();
			await RunTasks(stage2, () =>
		       {
		           if (!stage2.TryTake(out var item))
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

		           var word = item.Substring(0, len);
		           words.TryAdd(word, word); // ignore return, returns false if already added
		       });

			Console.WriteLine($"Found {words.Count - capCount} repeating words");

			var stageWords = words.Values;
			var input = stage3;
			var candidates = new List<string>();

			do
			{
				int startingCount = input.Count;
				var startingTime = DateTime.Now;
				Console.WriteLine($"Starting searching by dictionary. {input.Count:n0} input words. " +
													   $"{stageWords.Count:n0} dictionary words to check.");

				var newCandidates = new ConcurrentDictionary<string, string>();
				var nextStage = new ConcurrentBag<string>();

				Task completedTask;
				Task updateGuiTask;
				var workerTask = RunTasks(input, () =>
				{
					if (!input.TryTake(out var item))
						return;

					bool wasFound = false;
					foreach (var word in stageWords)
					{
						if (!item.StartsWith(word))
							continue;

						wasFound = true;
						var newCandidate = item.Substring(word.Length);
						if (newCandidate.Length != 0)
						{
							newCandidates.TryAdd(newCandidate, newCandidate);
						}

						break;
					}

					if (!wasFound)
						nextStage.Add(item);
				});

				do
				{
					// ReSharper disable once MethodSupportsCancellation
					updateGuiTask = Task.Delay(1000 /*, cancellationToken - don't pass, show status during the cancellation */)
						.ContinueWith(t =>
							{
								var now       = DateTime.Now;
								var elapsed   = now - startingTime;
								var leftItems = input.Count;
								var doneItems = startingCount - leftItems;
								var speed = doneItems / elapsed.TotalSeconds;
								if (speed != 0)
								{
									var remaining = TimeSpan.FromSeconds(leftItems / speed);
									Console.WriteLine($"{now}: {leftItems:n0} input words left. " +
									$"{newCandidates.Count:n0} new candidates discovered. ETA {remaining}");
								}

								Console.WriteLine($"{now}: {leftItems:n0} input words left. " +
									$"{newCandidates.Count:n0} new candidates discovered. ETA {remaining}");
							});
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

			var output = words.Values.ToList();
			output.Sort();
			WriteToFile(args[1], output);

			candidates.Sort();
			WriteToFile(args[2], candidates);

			output = input.ToList();
			output.Sort(); 
			WriteToFile(args[3], output);
		}

		static void WriteToFile(string path, IEnumerable<string> data)
		{
			File.WriteAllLines(path, data);
			//File.WriteAllText(path, string.Join("\n", lines) + "\n");
		}

		static async Task RunTasks(ConcurrentBag<string> input, Action action)
		{
			var tasks = new List<Task>();
			while (!input.IsEmpty)
			{
				var task = Task.Run(action);

				tasks.Add(task);
				await BalanceLoad(tasks);
			}
			await Task.WhenAll(tasks);
		}

		static async Task BalanceLoad(List<Task> tasks)
		{
			if (tasks.Count == 8)
			{
				var completedTask = await Task.WhenAny(tasks);
				tasks.Remove(completedTask);
				foreach (var t in tasks.ToArray())
				{
					if (t.IsCompleted)
						tasks.Remove(t);
				}
			}
		}
	}
}
