using NAudio.Wave;
using System.Net.Http.Json;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.Wave;

namespace ConversationLM;

[Obsolete]
public class AudioService
{
	public readonly SpeechRecognitionEngine Input = new(new System.Globalization.CultureInfo("fr-FR"));

	public readonly SpeechSynthesizer Output = new();

	public readonly HttpClient Client = new();

	public List<(string role, string content)> History = [];

	public AudioService()
	{
		Input.SetInputToDefaultAudioDevice();
		Input.LoadGrammar(new DictationGrammar());

		Output.SetOutputToDefaultAudioDevice();
		Output.SelectVoiceByHints(VoiceGender.Male, VoiceAge.Adult, 0, new System.Globalization.CultureInfo("fr-FR"));
		Output.Rate = 7; // 2 c'est okay, mais peut mieux faire. 5 c'est pas trop mal. 10 trop rapide. 7 c'est bien.
	}

	public static async Task RunAsync(SpeechRecognitionEngine input, SpeechSynthesizer output, HttpClient client, List<(string role, string content)> history)
	{
		Console.WriteLine("Assistant vocal prêt. Parle quand tu veux.");

		while (true)
		{
			Console.WriteLine("🎤 Enregistrement...");
			var text = await CaptureAsync(input);
			if (string.IsNullOrWhiteSpace(text) || text == " [Musique]" || text == " *musique*" || text == " *Musique*")
			{
				Console.WriteLine("…silence détecté, j'attends que tu parles.");
				continue;
			}
			Console.WriteLine($"Tu as dit : {text}");

			Console.WriteLine("🤖 Réponse IA...");
			var reply = await QueryLlmAsync(client, text, history);
			Console.WriteLine($"IA : {reply}");

			Console.WriteLine("🔊 Lecture...");
			await SpeakAsync(reply, output);
		}
	}

	public static async Task<string?> CaptureAsync(SpeechRecognitionEngine input)
	{
		var hasSpeech = await RecordToWavAsync("input.wav");
		if (!hasSpeech) return null;

		var modelName = "ggml-small.bin";
		if (!File.Exists(modelName))
		{
			using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Small);
			using var fileWriter = File.OpenWrite(modelName);
			await modelStream.CopyToAsync(fileWriter);
		}

		var whisperFactory = WhisperFactory.FromPath(modelName);
		var processor = whisperFactory.CreateBuilder().WithLanguage("fr").Build();
		using var wavStream = File.OpenRead("input.wav");

		var segmentBuilder = new StringBuilder();
		try
		{
			await foreach (var segment in processor.ProcessAsync(wavStream))
				segmentBuilder.Append(segment.Text);
		}
		catch (CorruptedWaveException) { }

		return segmentBuilder.ToString();
	}

	public static async Task<bool> RecordToWavAsync(string path)
	{
		using var waveIn = new WaveIn
		{
			WaveFormat = new WaveFormat(16000, 1)
		};

		using var writer = new WaveFileWriter(path, waveIn.WaveFormat);

		var silenceThreshold = 200;
		var silenceDuration = TimeSpan.FromSeconds(1);
		var lastSoundTime = DateTime.UtcNow;
		//bool hasSpeech = false;
		TimeSpan minSpeechDuration = TimeSpan.FromMilliseconds(100);
		DateTime? speechStart = null;

		waveIn.DataAvailable += (_, e) =>
		{
			writer.Write(e.Buffer, 0, e.BytesRecorded);

			// --- RMS (énergie moyenne) ---
			double sumSquares = 0;
			int samples = e.BytesRecorded / 2;

			for (int i = 0; i < e.BytesRecorded; i += 2)
			{
				short sample = BitConverter.ToInt16(e.Buffer, i);
				sumSquares += sample * sample;
			}

			double rms = Math.Sqrt(sumSquares / samples);

			bool isSpeech = rms > silenceThreshold;

			if (isSpeech)
			{
				speechStart ??= DateTime.UtcNow;
				lastSoundTime = DateTime.UtcNow;
			}
			
			//// Détection de parole
			//if (!hasSpeech)
			//	for (int i = 0; i < e.BytesRecorded; i += 2)
			//	{
			//		short sample = BitConverter.ToInt16(e.Buffer, i);
			//		if (Math.Abs(sample) > silenceThreshold)
			//		{
			//			hasSpeech = true;
			//			break;
			//		}
			//	}
		};

		waveIn.StartRecording();

		// Boucle d'attente dynamique
		while (true)
		{
			await Task.Delay(100);

			var silenceTime = DateTime.UtcNow - lastSoundTime;
			if (silenceTime >= silenceDuration)
				break;
		}

		waveIn.StopRecording();
		bool hasSpeech = speechStart.HasValue &&
					 (DateTime.UtcNow - speechStart.Value) >= minSpeechDuration;

		return hasSpeech;
	}

	public static async Task<string> QueryLlmAsync(HttpClient client, string text, List<(string role, string content)> history, string model = "llama3.1:8b")
	{
		history.Add(("User", text));

		var prompt = string.Join("\n", history.Select(h => $"{h.role}: {h.content}"));
		var response = await client.PostAsJsonAsync("http://localhost:11434/api/generate", new { model, prompt });
		//var response = await client.PostAsJsonAsync(
		//	"http://localhost:11434/api/generate",
		//	new StringContent($"{{\"model\":\"{model}\",\"prompt\":\"{prompt}\"}}"));
		//var response = await client.PostAsync(
		//	"http://localhost:11434/api/generate",
		//	new StringContent($"{{\"model\":\"{model}\",\"prompt\":\"{prompt}\"}}")
		//);
		//var response = await client.PostAsJsonAsync("http://localhost:11434/api/generate", new { model, prompt });
		/*
		Exemple de réponse : (prompt = "Bonjour, vous êtes toujours là ?")
HTTP Response Content: {"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.7719483Z","response":"Bonjour","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.7793263Z","response":" !","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.791841Z","response":" O","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.8014256Z","response":"ui","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.8110406Z","response":",","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.8209447Z","response":" je","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.8307686Z","response":" suis","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.8402837Z","response":" toujours","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.8501275Z","response":" là","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.8596888Z","response":" pour","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.8682015Z","response":" vous","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.8782759Z","response":" aider","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.8890298Z","response":".","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.8985437Z","response":" Comment","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.9075433Z","response":" puis","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.9170701Z","response":"-","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.9273425Z","response":"je","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.9358561Z","response":" vous","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.9463628Z","response":" aider","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.9559603Z","response":" aujourd","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.9657037Z","response":"'hui","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.9754028Z","response":" ?","done":false}
{"model":"llama3.1:8b","created_at":"2026-08-29T13:44:42.9855338Z","response":"","done":true,"done_reason":"stop","context":[128006,882,128007,271,82681,11,9189,62299,44093,39015,949,128009,128006,78191,128007,271,82681,758,507,2005,11,4864,36731,44093,39015,5019,9189,91878,13,12535,44829,12,3841,9189,91878,75804,88253,949],"total_duration":315271200,"load_duration":2163400,"prompt_eval_count":17,"prompt_eval_duration":81708000,"eval_count":23,"eval_duration":216526000}
		*/
		var raw = await response.Content.ReadAsStringAsync();
		//Console.WriteLine($"HTTP Response Content: {raw}");
		//return System.Text.Json.JsonSerializer.Deserialize<Llama3_1Response>(raw)!.response; //! flux NDJSON !

		var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		var finalText = new StringBuilder();

		foreach (var line in lines)
			try
			{
				var obj = JsonSerializer.Deserialize<Llama3_1Response>(line);
				if (obj != null)
					finalText.Append(obj.response.Replace(" **", ""));
			}
			catch { } // Ligne non JSON → on ignore

		var reply = finalText.ToString();
		history.Add(("Assistant", reply));
		return reply;
	}

	public static async Task SpeakAsync(string text, SpeechSynthesizer output)
	{
		output.SpeakAsyncCancelAll();
		output.SpeakAsync(text);

		await EventAwaiter.WaitForEventAsync<SpeakCompletedEventArgs, bool>(
			subscribe: h => output.SpeakCompleted += h,
			unsubscribe: h => output.SpeakCompleted -= h,
			resolver: (args, tcs) => tcs.TrySetResult(true),
			timeout: TimeSpan.FromSeconds(30)
		);

		//	var psi = new System.Diagnostics.ProcessStartInfo
		//	{
		//		FileName = "piper.exe",
		//		Arguments = $"--model fr_FR-mathieu-medium.onnx --output output.wav --text \"{text}\"",
		//		UseShellExecute = false
		//	};

		//	var process = System.Diagnostics.Process.Start(psi);
		//	process.WaitForExit();

		//using var audio = new AudioFileReader("output.wav");
		//using var output = new WaveOut();
		//output.Init(audio);
		//output.Play();

		//while (output.PlaybackState == PlaybackState.Playing)
		//	await Task.Delay(100);
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Styles d'affectation de noms", Justification = "Pas ici.")]
	private class LlmResponse
	{
		public string response { get; set; } = "";
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Styles d'affectation de noms", Justification = "Pas ici.")]
	private class Llama3_1Response : LlmResponse
	{
		public string model { get; set; } = "";
		public string created_at { get; set; } = "";
		public bool done { get; set; } = false;

		public string? done_reason { get; set; }
		public int[]? context { get; set; }
		public long? total_duration { get; set; }
		public long? load_duration { get; set; }
		public int? prompt_eval_count { get; set; }
		public long? prompt_eval_duration { get; set; }
		public int? eval_count { get; set; }
		public long? eval_duration { get; set; }
	}

	private static class EventAwaiter
	{
		public static async Task<T> WaitForEventAsync<TEventArgs, T>(
			Action<EventHandler<TEventArgs>> subscribe,
			Action<EventHandler<TEventArgs>> unsubscribe,
			Action<TEventArgs, TaskCompletionSource<T>> resolver,
			TimeSpan timeout)
			where TEventArgs : EventArgs
		{
			var tcs = new TaskCompletionSource<T>();

			void handler(object? _, TEventArgs args) => resolver(args, tcs);

			subscribe(handler);

			try
			{
				using var cts = new CancellationTokenSource(timeout);

				var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token));

				if (completed == tcs.Task)
					return await tcs.Task;

				return default!;
			}
			finally
			{
				unsubscribe(handler);
			}
		}
	}
}