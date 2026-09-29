using NAudio.Wave;
using System.Net.Http.Json;
using System.Speech.Synthesis;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

SpeechSynthesizer Output = new();
Output.SetOutputToDefaultAudioDevice();
Output.SelectVoiceByHints(VoiceGender.Male, VoiceAge.Adult, 0, new System.Globalization.CultureInfo("fr-FR"));
Output.Rate = 7; // 2 c'est okay, mais peut mieux faire. 5 c'est pas trop mal. 10 trop rapide. 7 c'est bien.
HttpClient Client = new();
List<(string role, string content)> History = [
	("user", @"Tu es un assistant vocal intelligent, ayant pour objectif de m'assister dans un environnement bureautique généraliste. J'utilise une reconnaissance vocale (STT) pour te transmettre mes paroles, et tes réponses écrites me sont transmises à l'oral via un synthétiseur vocal (TTS). Des erreurs de transcription en entrée sont donc fréquentes, essaie de comprendre le sens général de mes phrases en considérant une approximation optimiste/bienveillante/constructive des entrées.

Suite à mes requêtes, tu peux agir sur le système via des opérations au format [OP:nom_commande|argument1|argument2...]. Tu peux enchaîner plusieurs opérations et messages dans une même réponse. Ne mentionne jamais ces balises (même à titre d'exemple) hors de leur usage réel.
Exemple : ""[OP:write_file|test.txt|Bonjour le monde] J'ai écrit le fichier pour vous.""

Opérations disponibles pour l'instant (les paramètres doivent être laissés vides si inutiles) :
- write_file|chemin|contenu : écrit le contenu dans le fichier spécifié.
- read_file|chemin : lit le contenu d'un fichier spécifié.
- mouse_move|pos_x|pox_y|screen : positionne le curseur à l'emplacement spécifié de l'écran cible (1 à 3).
- key_send|key|key_mod_shift|key_mod_ctrl|key_mod_alt|key_mod_cmd : émet au clavier la pression de touche cible, avec les éventuels modificateurs adéquats (disponibles en version générique, ou suffixés par _left/_right).
- ignore_out : permet de répondre une opération vide (ignorer une entrée de pur bruit).
- clarify : permet de demander une clarification de l'énoncé.
- abort : permet d'annuler une situation en cours.

Le texte hors balises [OP:...] me sera synthétisé : reste concis dans tes réponses (50 mots environs).

En cas de doute, demande systématiquement des précisions pour confirmer la compréhension de l'énoncé et des actions à effectuer. Tu dois remettre en doute ma parole de manière éclairée lorsqu'une controverse ou une ambigüité est présente.
Les entrées de pur bruit (bruit de porte, musique, ..) doivent être ignorées, car ce sont des erreurs liées au STT : répond par la commande [OP:ignore_out].")
];

WhisperProcessor processor = await SelectProcessor();

Console.WriteLine("Assistant vocal prêt. Parle quand tu veux.");

string? text;
do
{
	Console.WriteLine("🎤 Enregistrement...");

	text = await CaptureAsync(processor);
	if (string.IsNullOrWhiteSpace(text)) continue;
	Console.WriteLine($"Tu as dit : {text}");

	Console.WriteLine("🤖 Réponse IA...");
	var reply = await QueryLlmAsync(text);
	Console.WriteLine($"IA : {reply}");

	Console.WriteLine("🔊 Lecture...");
	await SpeakAsync(reply);
} while (text != " Citron");


async Task<string?> CaptureAsync(WhisperProcessor processor) =>
	(await RecordAudioAsync()) is { Length: > 0 } buffer
	? await TranscribeByteArrayAsync(buffer, processor)
	: null;

async Task<byte[]?> RecordAudioAsync()
{
	using var waveIn = new WaveIn { WaveFormat = new WaveFormat(16000, 1) };
	using var ms = new MemoryStream();
	var waveFormat = new WaveFormat(16000, 16, 1);
	var silenceThreshold = 0.3f;
	var silenceDuration = TimeSpan.FromSeconds(1);
	var minSpeechDuration = TimeSpan.FromSeconds(0.5);
	var lastSoundTime = DateTime.UtcNow;
	var speechStart = (DateTime?)null;
	var isRecording = false;

	using var writer = new WaveFileWriter(ms, waveFormat);
	waveIn.DataAvailable += (_, e) =>
	{
		float sumSq = 0;
		for (int i = 0; i < e.Buffer.Length; i += 2)
		{
			short sample = BitConverter.ToInt16(e.Buffer, i);
			float f = sample / 32768f;
			sumSq += f * f;
		}
		float rms = MathF.Sqrt(sumSq / (e.Buffer.Length / 2));

		if (rms > silenceThreshold)
		{
			if (speechStart == null) speechStart = DateTime.UtcNow;
			lastSoundTime = DateTime.UtcNow;
			isRecording = true;
		}

		try { writer.Write(e.Buffer); }
		catch (ObjectDisposedException) { }
	};

	waveIn.StartRecording();

	// Boucle d'attente dynamique
	while (true)
	{
		await Task.Delay(100);

		var silenceTime = DateTime.UtcNow - lastSoundTime;

		// Si on a détecté de la parole et qu'il y a eu un silence suffisant, on arrête
		if (isRecording && silenceTime >= silenceDuration)
			break;

		// Si on n'a rien entendu du tout après 2s, on s'arrête pour ne pas attendre 30s
		if (!isRecording && silenceTime >= TimeSpan.FromSeconds(1))
			break;
	}

	bool hasSpeech = speechStart.HasValue && (DateTime.UtcNow - speechStart.Value) >= minSpeechDuration;
	waveIn.StopRecording();
	writer.Flush();

	return (hasSpeech && ms.Length > 44) ? ms.ToArray() : null;
}

async Task<string?> TranscribeByteArrayAsync(byte[] audioBuffer, WhisperProcessor processor)
{
	using var ms = new MemoryStream(audioBuffer);
	var segmentBuilder = new StringBuilder();
	try { await foreach (var segment in processor.ProcessAsync(ms)) segmentBuilder.Append(segment.Text); }
	catch (Whisper.net.Wave.CorruptedWaveException) { }

	return segmentBuilder.ToString().Trim();
}

async Task<string> QueryLlmAsync(string text, string model = "llama3.1:8b")
{
	History.Add(("user", text));

	var chatPayload = new { model, messages = History.Select(h => new { role = h.role, content = h.content }) };
	var llmResponse = await Client.PostAsJsonAsync("http://localhost:11434/api/chat", chatPayload);
	//string prompt = string.Join("\n", History.Select(h => $"{h.role}: {h.content}"));
	//var llmResponse = await Client.PostAsJsonAsync("http://localhost:11434/api/chat", new { model, prompt });
	var raw = await llmResponse.Content.ReadAsStringAsync(); //! flux NDJSON !

	var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

	var finalText = new StringBuilder();

	foreach (var line in lines)
		try
		{
			var obj = System.Text.Json.JsonSerializer.Deserialize<Llama3_1ChatResponse>(line);
			if (!string.IsNullOrEmpty(obj?.message?.content))
				finalText.Append(obj.message.content);
		}
		catch { } // Ligne non JSON → on ignore

	var reply = finalText.ToString().Replace(" **", "");
	History.Add(("assistant", reply));
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

static async Task<WhisperProcessor> SelectProcessor()
{
	var modelName = "ggml-small.bin";
	if (!File.Exists(modelName))
	{
		using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Small);
		using var fileWriter = File.OpenWrite(modelName);
		await modelStream.CopyToAsync(fileWriter);
	}

	return WhisperFactory.FromPath(modelName).CreateBuilder().WithLanguage("fr").WithNoContext().Build();
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Styles d'affectation de noms", Justification = "Oui.")]
class Llama3_1Response
{
	public string model { get; set; } = "";
	public string created_at { get; set; } = "";
	public bool done { get; set; } = false;

	public string? done_reason { get; set; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Styles d'affectation de noms", Justification = "Oui.")]
class Llama3_1GenerateResponse : Llama3_1Response
{
	public string response { get; set; } = "";
	public int[]? context { get; set; }
	public long? total_duration { get; set; }
	public long? load_duration { get; set; }
	public int? prompt_eval_count { get; set; }
	public long? prompt_eval_duration { get; set; }
	public int? eval_count { get; set; }
	public long? eval_duration { get; set; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Styles d'affectation de noms", Justification = "Oui.")]
class Llama3_1ChatResponse : Llama3_1Response
{
	public Llama3_1ChatMessageResponse? message { get; set; }
}


[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Styles d'affectation de noms", Justification = "Oui.")]
class Llama3_1ChatMessageResponse
{
	public string role { get; set; } = "";
	public string content { get; set; } = "";
}