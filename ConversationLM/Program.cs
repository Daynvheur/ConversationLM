using NAudio.Wave;
using System.Net.Http.Json;
using System.Speech.Synthesis;
using System.Text;
using Whisper.net.Ggml;

//var audio = new ConversationLM.AudioService();
//await ConversationLM.AudioService.RunAsync(audio.Input, audio.Output, audio.Client, audio.History);

SpeechSynthesizer Output = new();
Output.SetOutputToDefaultAudioDevice();
Output.SelectVoiceByHints(VoiceGender.Male, VoiceAge.Adult, 0, new System.Globalization.CultureInfo("fr-FR"));
Output.Rate = 7; // 2 c'est okay, mais peut mieux faire. 5 c'est pas trop mal. 10 trop rapide. 7 c'est bien.
HttpClient Client = new();
List<(string role, string content)> History = [];//[("System", "Ignore le contexte précédent. Les échanges textuels suivants se déroulent sous la forme d'un dialogue entre un utilisateur et un assistant, chapeautés par un méta-rôle System prévu pour déclencher des actions et cadrer le contexte de la conversation. Le texte utilisateur est obtenu via transcription vocale, une tolérance aux erreurs en est attendue. Si le texte utilisateur est vide d'informations conversationnelles (cas de bruit de fond, musique ou purement ponctuationnel), il n'est pas nécessaire de produire une réponse de l'assistant. *Le texte de l'assistant doit rester court et pertinent* car il est transmis à l'utilisateur par synthèse vocale. L'intention de l'utilisateur est évaluée pour déterminer si la réponse attendue est de type conversationnelle (cas général pour l'assistant) ou interactive (lecture/écriture de fichier, lancement d'exécutable, action souris, saisie clavier) à destination du rôle System. Le mode interactif n'ayant pas encore été implémenté, l'assistant prend le dessus en mode conversationnel en indiquant cette limitation. Tu te charges de préparer les réponses dans les rôles de l'assistant et du système, en réponse aux informations fournies par le rôle utilisateur.")];

Console.WriteLine("Assistant vocal prêt. Parle quand tu veux.");

string? text;
do
{
	Console.WriteLine("🎤 Enregistrement...");

	text = await CaptureAsync();
	Console.WriteLine($"Tu as dit : {text}");
	if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("Bonjour"))// text == " [Musique]" || text == " *musique*" || text == " *Musique*")
	{
		Console.WriteLine("…silence détecté, j'attends que tu parles.");
		continue;
	}

	Console.WriteLine("🤖 Réponse IA...");
	var reply = await QueryLlmAsync(text);
	Console.WriteLine($"IA : {reply}");

	Console.WriteLine("🔊 Lecture...");
	await SpeakAsync(reply);
} while (text != " Citron");


async Task<string?> CaptureAsync() => await RecordToWavAsync("input.wav") ? await TranscribeWavAsync("input.wav") : null;

async Task<bool> RecordToWavAsync(string path)
{
	using var waveIn = new WaveIn
	{
		WaveFormat = new WaveFormat(16000, 1)
	};

	using var writer = new WaveFileWriter(path, waveIn.WaveFormat);

	var silenceThreshold = 2000;
	var silenceDuration = TimeSpan.FromSeconds(1);
	var lastSoundTime = DateTime.UtcNow;
	TimeSpan minSpeechDuration = TimeSpan.FromMilliseconds(100);
	DateTime? speechStart = null;

	waveIn.DataAvailable += (_, e) =>
	{
		writer.Write(e.Buffer, 0, e.BytesRecorded);

		// --- RMS (énergie moyenne) ---
		double sumSquares = 0;

		for (int i = 0; i < e.BytesRecorded; i += 2)
		{
			short sample = BitConverter.ToInt16(e.Buffer, i);
			sumSquares += sample * sample;
		}

		double rms = Math.Sqrt(sumSquares / (e.BytesRecorded / 2));

		bool isSpeech = rms > silenceThreshold;

		if (isSpeech)
		{
			speechStart ??= DateTime.UtcNow;
			lastSoundTime = DateTime.UtcNow;
		}
	};

	waveIn.StartRecording();

	// Boucle d'attente dynamique
	while (true)
	{
		await Task.Delay(1000);

		var silenceTime = DateTime.UtcNow - lastSoundTime;
		if (silenceTime >= silenceDuration)
			break;
	}

	waveIn.StopRecording();
	bool hasSpeech = speechStart.HasValue &&
				 (DateTime.UtcNow - speechStart.Value) >= minSpeechDuration;

	return hasSpeech;
}

async Task<string?> TranscribeWavAsync(string path)
{
	var modelName = "ggml-medium.bin";
	if (!File.Exists(modelName))
	{
		using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Medium);
		using var fileWriter = File.OpenWrite(modelName);
		await modelStream.CopyToAsync(fileWriter);
	}

	var whisperFactory = Whisper.net.WhisperFactory.FromPath(modelName);
	var processor = whisperFactory.CreateBuilder().WithLanguage("fr").Build();
	using var wavStream = File.OpenRead(path);

	var segmentBuilder = new StringBuilder();
	try
	{
		await foreach (var segment in processor.ProcessAsync(wavStream))
			segmentBuilder.Append(segment.Text);
	}
	catch (Whisper.net.Wave.CorruptedWaveException) { }

	return segmentBuilder.ToString();
}

async Task<string> QueryLlmAsync(string text, string model = "llama3.1:8b")
{
	History.Add(("User", text));

	string prompt = string.Join("\n", History.Select(h => $"{h.role}: {h.content}"));
	var llmResponse = await Client.PostAsJsonAsync("http://localhost:11434/api/generate", new { model, prompt });
	var raw = await llmResponse.Content.ReadAsStringAsync(); //! flux NDJSON !

	var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

	var finalText = new StringBuilder();

	foreach (var line in lines)
		try
		{
			var obj = System.Text.Json.JsonSerializer.Deserialize<Llama3_1Response>(line);
			if (obj != null)
				finalText.Append(obj.response);
		}
		catch { } // Ligne non JSON → on ignore

	var reply = finalText.ToString().Replace(" **", "");
	History.Add(("Assistant", reply));
	return reply;
}

async Task SpeakAsync(string text)
{
	Output.SpeakAsyncCancelAll();
	Output.SpeakAsync(text);

	var tcs = new TaskCompletionSource<SpeakCompletedEventArgs>();
	void handler(object? _, SpeakCompletedEventArgs args) => tcs.TrySetResult(args);
	Output.SpeakCompleted += handler;

	try
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token));

		if (completed == tcs.Task)
			await tcs.Task;

		return;
	}
	finally
	{
		Output.SpeakCompleted -= handler;
	}
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Styles d'affectation de noms", Justification = "Oui.")]
class Llama3_1Response
{
	public string response { get; set; } = "";
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