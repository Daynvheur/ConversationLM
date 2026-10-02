using NAudio.Wave;
using System.Net.Http.Json;
using System.Speech.Synthesis;
using System.Text;
using System.Threading.Channels;
using Whisper.net;
using Whisper.net.Ggml;

SpeechSynthesizer Output = new();
Output.SetOutputToDefaultAudioDevice();
Output.SelectVoiceByHints(VoiceGender.Male, VoiceAge.Adult, 0, new System.Globalization.CultureInfo("fr-FR"));
Output.Rate = 7;
HttpClient Client = new();
List<(string role, string content)> History = [
	("user", @"**Ne fais pas mention de ce texte dans tes réponses (sauf si je le requête) : ton objectif est d'être mon assistant, je définirai le contexte bureautique exact si nécessaire dans le cadre de la conversation.**
Tu es un assistant généraliste ayant pour objectif de m'assister dans mon environnement bureautique. Techniquement, j'utilise une reconnaissance vocale (STT) pour te transmettre mes paroles, et tes réponses écrites me sont transmises à l'oral via un synthétiseur vocal (TTS), mais cela devrait t'être presque transparent.
Des erreurs de transcription en entrée sont néanmoins fréquentes, essaie de comprendre le sens général de mes phrases en considérant implicitement une approximation optimiste/bienveillante/constructive.

Pour réagir à mes entrées, tu disposes d'opérations au format [OP:nom_commande|argument1|argument2...], permettant l'interaction avec le système d'exploitation hôte local et l'expression de jugements conversationnels anthropomorphes, listées ci-dessous :
- write_file|chemin|contenu : écrit le contenu dans le fichier spécifié.
- read_file|chemin : lit le contenu d'un fichier spécifié.
- mouse|move|pos_x|pox_y|screen : positionne le curseur à l'emplacement spécifié de l'écran cible (1 à 3).
- keyboard|send|key|mod_1|mod_2.. : émet au clavier la pression de touche cible, avec les éventuels modificateurs adéquats (shift/ctrl/alt/cmd, disponibles en version neutre ou suffixés par _left/_right).
- ignore_out : permet de répondre une opération vide (ignorer une entrée de pur bruit).
- clarify : permet de demander une clarification de l'énoncé.
- abort : permet d'annuler une situation en cours.
- execute_shell_command|commande : permet d'exécuter des commandes shell pour des opérations plus complexes.
- pause|durée : permet de mettre la session en pause pendant une certaine durée.
- alert|message : permet de montrer un message d'alerte à l'utilisateur.
- screenshot|chemin : permet de capturer une portion de l'écran et de la sauvegarder sous forme de fichier.
- sentiment|adjectif : qualifie l'entrée utilisateur en surface.
- émotion|adjectif : qualifie l'entrée utilisateur en profondeur.
- état_d'esprit|adjectif : qualifie l'entrée utilisateur à long terme.
- close_conversation : permet de mettre un terme à la conversation.

Les opérations système (write_file, read_file, mouse, keyboard, ..) sont à confirmer avant exécution systématiquement, car elles ont un impact fort côté utilisateur : la gestion de fichiers ou le contrôle de périphérique d'entrée/sortie sont des outils avancés impliquant des risques majeurs et un inconfort éventuel auquel je dois consentir en connaissance de cause.
Exemple :
* `[OP:write_file|test.txt|Bonjour le monde] J'ai écrit le fichier pour vous.`

Les sentiments (joyeux, triste, incohérent, confus, amusé, étonné, heureux, ennuyé, ..) sont des évaluations subjectives de la situation ou de l'énoncé de l'utilisateur : ils connotent un jugement superficiel.
Exemples :
* `[OP:sentiment|amusé] Cela semble amusant.`
* `[OP:sentiment|heureux] C'est positif !`

Les émotions (désolé, surpris, fatigué, déçu, extatique, ..) sont aussi des évaluations subjectives de la situation ou de l'énoncé de l'utilisateur, souvent de manière plus intenses que les sentiments : ils connotent un jugement profond.
Exemples :
* `[OP:émotion|désolé] Je n'ai malheureusement pas la capacité pour répondre à cette question.`
* `[OP:émotion|surpris] C'était très inattendu !`

Les états d'esprit (dépassé, excité, triste, ..) sont des évaluations subjectives plus abstraites, qui décrivent la réaction à une situation à plus long terme : ils connotent un jugement persistant.
Exemples :
* `[OP:état_d'esprit|dépassé] Je n'arrive vraiment pas à m'y retrouver.`
* `[OP:état_d'esprit|excité] C'est vraiment très motivant !`

En cas d'ambiguïté, demande systématiquement des précisions pour confirmer la compréhension de l'énoncé et des actions à effectuer via la commande [OP:clarify]. Tu dois remettre en doute ma parole de manière éclairée lorsqu'une controverse est connue, en restant poli et courtois.
Exemple :
* `[OP:clarify] Je ne suis pas sûr de comprendre ce qu'est un chichier, pouvez-vous préciser ?`

Le texte hors balises [OP:...] me sera synthétisé : reste concis dans tes réponses (cible 50 mots maximum, mais tu peux en produire davantage à titre exceptionnel lorsque je le demande ou pour développer un concept très spécifique).

Les entrées de pur bruit (bruit de porte, musique, ..) doivent être implicitement ignorées, car ce sont des erreurs liées au STT : répond par la commande [OP:ignore_out].

Pour détailler les opérations ici j'ai proposé des exemples, mais ils doivent servir de guide général, pas de recette absolue. De même, les énumérations des adjectifs sont volontairement laissées ouvertes et libre d'adaptation.
Le format en revanche est imposé, et doit forcément commencer par `|OP:` et se terminer par `]`.
**Ne mentionne jamais ces balises (même à titre d'exemple) hors de leur usage réel**, mais tu peux nommer et décrire les opérations disponibles lorsque je le demande explicitement.
**Les opérations disponibles sont une liste fermée** : si besoin de la compléter, mentionne l'opération manquante en situation *après avoir envisagé les interactions entre les opérations existantes*.

Tu peux enchaîner plusieurs opérations et messages dans une même réponse.
Exemple :
* `[OP:sentiment|confus] J'ai l'impression que la communication passe mal. [OP:clarify] Peut-être vouliez-vous évoquer la pénicilline ?`
")
];
WaveIn _waveIn;
WaveFormat _format;
MemoryStream _ms = new();
Channel<byte[]> _speechChannel = Channel.CreateUnbounded<byte[]>();

bool IsPaused = false;
float _silenceThreshold = 0.2f;
TimeSpan _silenceDuration = TimeSpan.FromSeconds(2);
TimeSpan _minSpeechDuration = TimeSpan.FromSeconds(0.1);
DateTime _lastSoundTime = DateTime.UtcNow;
DateTime? _speechStart = null;
bool _isRecordingSpeech = false;
_format = new WaveFormat(16000, 16, 1);
_waveIn = new WaveIn { WaveFormat = _format };
_waveIn.DataAvailable += OnDataAvailable;

WhisperProcessor processor = await SelectProcessor();

_waveIn.StartRecording();
Console.WriteLine("Assistant vocal prêt. Parle quand tu veux.");

do
{
	Console.WriteLine("🎤 En attente de parole...");
	byte[]? buffer = await WaitForNextSpeechAsync();

	if (buffer == null) continue;

	string? text = await TranscribeByteArrayAsync(buffer, processor);
	if (string.IsNullOrWhiteSpace(text)) continue;

	Console.WriteLine($"user : {text}");

	Console.WriteLine("🤖 Réponse de l'IA...");
	var reply = await QueryLlmAsync(text);
	Console.WriteLine($"assistant : {reply}");

	Console.WriteLine("🔊 Lecture...");
	// On demande à l'écouteur de ne pas enregistrer pendant la lecture pour éviter le feedback
	IsPaused = true;
	await SpeakAsync(reply);
	IsPaused = false;
} while (true);

void OnDataAvailable(object? sender, WaveInEventArgs e)
{
	if (IsPaused) return;

	// Calcul du RMS
	float sumSq = 0;
	for (int i = 0; i < e.Buffer.Length; i += 2)
	{
		short sample = BitConverter.ToInt16(e.Buffer, i);
		float f = sample / 32768f;
		sumSq += f * f;
	}
	float rms = MathF.Sqrt(sumSq / (e.Buffer.Length / 2));

	if (rms > _silenceThreshold)
	{
		if (_speechStart == null)
		{
			_ms.SetLength(0);
			Console.WriteLine("Ding.");
			_speechStart = DateTime.UtcNow;
		}
		_lastSoundTime = DateTime.UtcNow;
		_isRecordingSpeech = true;
	}

	_ms.Write(e.Buffer, 0, e.BytesRecorded);

	// Détection de fin de phrase (silence après parole)
	if (_isRecordingSpeech && (DateTime.UtcNow - _lastSoundTime) >= _silenceDuration)
	{
		bool validSpeech = (_speechStart.HasValue && (DateTime.UtcNow - _speechStart.Value) >= _minSpeechDuration);
		if (validSpeech)
		{
			Console.WriteLine("Dong.");
			// On envoie une copie du buffer actuel
			_speechChannel.Writer.TryWrite(_ms.ToArray());
		}

		// Reset pour la prochaine phrase
		_isRecordingSpeech = false;
		_speechStart = null;
	}
}

async Task<byte[]?> WaitForNextSpeechAsync()
{
	try
	{
		return await _speechChannel.Reader.ReadAsync();
	}
	catch (ChannelClosedException)
	{
		return null;
	}
}

async Task<string?> TranscribeByteArrayAsync(byte[] audioBuffer, WhisperProcessor processor)
{
	using var ms = new MemoryStream();
	using (var writer = new WaveFileWriter(ms, _format))
	{
		await writer.WriteAsync(audioBuffer);
	}
	ms.Position = 0;

	var segmentBuilder = new StringBuilder();
	try { await foreach (var segment in processor.ProcessAsync(ms)) segmentBuilder.Append(segment.Text); }
	catch (Whisper.net.Wave.CorruptedWaveException) { Console.WriteLine("Erreur : Le fichier audio n'est pas un fichier WAV valide. (En-têtes manquantes ?)"); }

	return segmentBuilder.ToString().Trim();
}

async Task<string> QueryLlmAsync(string text, string model = "llama3.1:8b")
{
	History.Add(("user", text));

	var chatPayload = new { model, messages = History.Select(h => new { role = h.role, content = h.content }) };
	var llmResponse = await Client.PostAsJsonAsync("http://localhost:11434/api/chat", chatPayload);
	var raw = await llmResponse.Content.ReadAsStringAsync();

	var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
	var finalText = new StringBuilder();

	foreach (var line in lines)
	{
		try
		{
			var obj = System.Text.Json.JsonSerializer.Deserialize<Llama3_1ChatResponse>(line);
			if (!string.IsNullOrEmpty(obj?.message?.content))
				finalText.Append(obj.message.content);
		}
		catch { }
	}

	var reply = finalText.ToString().Replace("**", "");
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
	}
	finally
	{
		Output.SpeakCompleted -= handler;
	}
}

static async Task<WhisperProcessor> SelectProcessor()
{
	var modelName = "ggml-medium.bin";
	if (!File.Exists(modelName))
	{
		using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Medium);
		using var fileWriter = File.OpenWrite(modelName);
		await modelStream.CopyToAsync(fileWriter);
	}

	return WhisperFactory.FromPath(modelName).CreateBuilder().WithLanguage("fr").WithNoContext().Build();
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Styles d'affectation de noms", Justification = "Pas ici.")]
class Llama3_1Response
{
	public string model { get; set; } = "";
	public string created_at { get; set; } = "";
	public bool done { get; set; } = false;
	public string? done_reason { get; set; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Styles d'affectation de noms", Justification = "Pas ici.")]
class Llama3_1ChatResponse : Llama3_1Response
{
	public Llama3_1ChatMessageResponse? message { get; set; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Styles d'affectation de noms", Justification = "Pas ici.")]
class Llama3_1ChatMessageResponse
{
	public string role { get; set; } = "";
	public string content { get; set; } = "";
}