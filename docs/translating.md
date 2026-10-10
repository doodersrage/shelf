# Translating Shelf

Shelf's pages are in English, Spanish, French, and German. The Spanish, French, and German were written by an AI model and checked by script for consistency, but **no native speaker has read them yet**. Corrections are very welcome, a single sentence or a whole language.

## Where the words are

| What | File |
| --- | --- |
| Every sentence the server shows: pages, messages, emails | `src/Shelf.Api/Localization/es.json`, `fr.json`, `de.json` |
| Sentences the browser shows by itself: uploads, the reader, the offline page, the barcode scanner | `src/Shelf.Api/wwwroot/words.js`, one block per language |

Each table maps the English sentence, exactly as it is in the code, to its translation:

```json
"Send to Kindle": "Enviar al Kindle",
"Page {0} of {1}": "Página {0} de {1}",
```

To correct a translation, change the text on the right and leave the English on the left alone. Keep:

- **Placeholders** such as `{0}` and `{1}`: each must stay, though the order can change to suit the language.
- **HTML** such as `<strong>…</strong>` or `<code>Backup:Folder</code>`, as it is.
- **Names** of other software and settings: KOReader, Calibre, `Import:Folder`.

## The voice

Shelf talks to one reader, plainly and warmly:

- **Spanish** uses *tú*, **French** *vous*, **German** *du*.
- A page's name in a sentence ("on **Account**", "under **Loans**") is that page's name in the menu, translated the same way, so readers find it.
- One English word, one translation, everywhere. The current choices:

| English | Español | Français | Deutsch |
| --- | --- | --- | --- |
| e-book | libro electrónico | livre numérique | E-Book |
| audiobook | audiolibro | livre audio | Hörbuch |
| highlight | subrayado | surlignage | Markierung |
| loan, lend, borrow | préstamo, prestar, pedir prestado | prêt, prêter, emprunter | Ausleihe, verleihen, leihen |
| passkey | llave de acceso | clé d'accès | Passkey |
| two-step sign-in | verificación en dos pasos | connexion en deux étapes | zweistufige Anmeldung |
| single sign-on | inicio de sesión único | connexion unique | Einmalanmeldung |
| tag | etiqueta | étiquette | Schlagwort |
| shelf (your library) | estantería | bibliothèque | Regal |

French puts a non-breaking space before `: ; ? !` and inside `« »`.

## Checking a change

```bash
dotnet test --filter TranslationTests
```

The tests fail if a sentence on a page is missing from a language, if a translation loses or gains a placeholder, or if a table holds a sentence the code no longer uses. Then run Shelf and look: choose the language under **Language and region** on **Account**.

## Adding a language

1. Add it to `Languages` in `src/Shelf.Api/Localization/Words.cs`.
2. Copy `es.json` to the new code, such as `it.json`, and translate the right-hand side.
3. Add a block for it in `wwwroot/words.js`.
4. Add the code to `Languages` in `tests/Shelf.Api.Tests/TranslationTests.cs`, and run the tests.

Not a programmer? [Open a translation issue](https://github.com/doodersrage/shelf/issues/new?template=translation.yml) with the sentence and what it should say.
