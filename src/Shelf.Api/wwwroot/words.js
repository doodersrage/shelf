// The browser's own sentences in the page's language. Pages say their language in <html lang>; the offline page,
// which the server does not draw, uses the last language a page was in. English is the key, and a sentence not in
// a table shows in English. t("Sent {0}%", 40) puts values in place of {0}, {1}, and so on.
(() => {
  const tables = {
    es: {
      "That did not work. Try again.": "No ha funcionado. Inténtalo de nuevo.",
      "The passkey was not used. Try again when you are ready.": "No se ha usado la llave de acceso. Inténtalo de nuevo cuando quieras.",
      "This device already has a passkey for this shelf.": "Este dispositivo ya tiene una llave de acceso para este Shelf.",
      "Sending…": "Enviando…",
      "Sent {0}%": "Enviado {0} %",
      "Sent. Shelf is putting it away…": "Enviado. Shelf lo está guardando…",
      "The upload stopped. Check the connection and try again.": "La subida se ha detenido. Comprueba la conexión e inténtalo de nuevo.",
      "This book's file is no longer on this device.": "El archivo de este libro ya no está en este dispositivo.",
      "On this page": "En esta página",
      "In this chapter": "En este capítulo",
      "On this device; sent to the shelf when you are online.": "En este dispositivo; se enviará a Shelf cuando tengas conexión.",
      "Online again": "Con conexión de nuevo",
      "Offline": "Sin conexión",
      "Page {0} of {1}": "Página {0} de {1}",
      "Chapter {0} of {1}": "Capítulo {0} de {1}",
      "Remove": "Quitar",
      "Kept for reading offline": "Guardados para leer sin conexión",
      "The shelf cannot be reached right now. The books you kept on this device are here, and where you stop is sent back when you are online again.": "No se puede acceder a Shelf ahora mismo. Aquí están los libros que guardaste en este dispositivo, y el punto donde te detengas se enviará cuando vuelvas a tener conexión.",
      "Nothing kept on this device": "No hay nada guardado en este dispositivo",
      "When you are online, open a book with an e-book and choose Keep for reading offline.": "Cuando tengas conexión, abre un libro con libro electrónico y elige Guardar para leer sin conexión.",
      "Kept books": "Libros guardados",
      "Previous": "Anterior",
      "Next": "Siguiente",
      "Text size": "Tamaño del texto",
      "Smaller": "Más pequeño",
      "Default": "Normal",
      "Larger": "Más grande",
      "Large": "Grande",
      "Largest": "Muy grande",
      "Note, if you like": "Nota, si quieres",
      "Highlight": "Subrayar",
      "Cancel": "Cancelar",
      "Highlights made here are kept on this device and sent to the shelf when you are online again.": "Los subrayados que hagas aquí se guardan en este dispositivo y se envían a Shelf cuando vuelvas a tener conexión.",
      "Scan the barcode": "Escanear el código de barras",
      "Hold the barcode on the back of the book inside the frame.": "Coloca el código de barras de la contraportada dentro del marco.",
      "No ISBN barcode was found. Try again closer, with more light, or type the number in.": "No se ha encontrado ningún código ISBN. Inténtalo más cerca, con más luz, o escribe el número.",
      "The camera could not be opened here. Press Scan the barcode again to take a photo of it instead.": "No se ha podido abrir la cámara aquí. Pulsa de nuevo Escanear el código de barras para hacerle una foto.",
      "Reading the barcode…": "Leyendo el código de barras…",
      "That photo could not be read. Try again, or type the number in.": "No se ha podido leer esa foto. Inténtalo de nuevo o escribe el número.",
      "Previous chapter": "Capítulo anterior",
      "Back": "Atrás",
      "Forward": "Adelante",
      "Next chapter": "Capítulo siguiente",
      "Slower": "Más lento",
      "Faster": "Más rápido",
      "Place in this chapter": "Posición en este capítulo",
      "Play": "Reproducir",
      "Pause": "Pausa",
      "{0} left": "quedan {0}",
      "Kept for listening": "Guardados para escuchar",
      "Or open an audiobook and choose Keep for listening offline.": "O abre un audiolibro y elige Guardar para escuchar sin conexión.",
    },
    fr: {
      "That did not work. Try again.": "Cela n'a pas fonctionné. Réessayez.",
      "The passkey was not used. Try again when you are ready.": "La clé d'accès n'a pas été utilisée. Réessayez quand vous voulez.",
      "This device already has a passkey for this shelf.": "Cet appareil a déjà une clé d'accès pour ce Shelf.",
      "Sending…": "Envoi…",
      "Sent {0}%": "Envoyé à {0} %",
      "Sent. Shelf is putting it away…": "Envoyé. Shelf le range…",
      "The upload stopped. Check the connection and try again.": "L'envoi s'est arrêté. Vérifiez la connexion et réessayez.",
      "This book's file is no longer on this device.": "Le fichier de ce livre n'est plus sur cet appareil.",
      "On this page": "Sur cette page",
      "In this chapter": "Dans ce chapitre",
      "On this device; sent to the shelf when you are online.": "Sur cet appareil ; envoyé à Shelf quand vous serez en ligne.",
      "Online again": "De nouveau en ligne",
      "Offline": "Hors ligne",
      "Page {0} of {1}": "Page {0} sur {1}",
      "Chapter {0} of {1}": "Chapitre {0} sur {1}",
      "Remove": "Retirer",
      "Kept for reading offline": "Gardés pour la lecture hors ligne",
      "The shelf cannot be reached right now. The books you kept on this device are here, and where you stop is sent back when you are online again.": "Shelf est injoignable pour le moment. Les livres gardés sur cet appareil sont ici, et l'endroit où vous vous arrêtez sera renvoyé quand vous serez de nouveau en ligne.",
      "Nothing kept on this device": "Rien n'est gardé sur cet appareil",
      "When you are online, open a book with an e-book and choose Keep for reading offline.": "Quand vous êtes en ligne, ouvrez un livre qui a un livre numérique et choisissez Garder pour la lecture hors ligne.",
      "Kept books": "Livres gardés",
      "Previous": "Précédent",
      "Next": "Suivant",
      "Text size": "Taille du texte",
      "Smaller": "Plus petit",
      "Default": "Normal",
      "Larger": "Plus grand",
      "Large": "Grand",
      "Largest": "Très grand",
      "Note, if you like": "Note, si vous voulez",
      "Highlight": "Surligner",
      "Cancel": "Annuler",
      "Highlights made here are kept on this device and sent to the shelf when you are online again.": "Les surlignages faits ici sont gardés sur cet appareil et envoyés à Shelf quand vous serez de nouveau en ligne.",
      "Scan the barcode": "Scanner le code-barres",
      "Hold the barcode on the back of the book inside the frame.": "Placez le code-barres au dos du livre dans le cadre.",
      "No ISBN barcode was found. Try again closer, with more light, or type the number in.": "Aucun code-barres ISBN n'a été trouvé. Réessayez de plus près, avec plus de lumière, ou saisissez le numéro.",
      "The camera could not be opened here. Press Scan the barcode again to take a photo of it instead.": "La caméra n'a pas pu s'ouvrir ici. Appuyez de nouveau sur Scanner le code-barres pour le prendre en photo.",
      "Reading the barcode…": "Lecture du code-barres…",
      "That photo could not be read. Try again, or type the number in.": "Cette photo n'a pas pu être lue. Réessayez, ou saisissez le numéro.",
      "Previous chapter": "Chapitre précédent",
      "Back": "Reculer",
      "Forward": "Avancer",
      "Next chapter": "Chapitre suivant",
      "Slower": "Plus lent",
      "Faster": "Plus rapide",
      "Place in this chapter": "Position dans ce chapitre",
      "Play": "Lecture",
      "Pause": "Pause",
      "{0} left": "encore {0}",
      "Kept for listening": "Gardés pour l'écoute",
      "Or open an audiobook and choose Keep for listening offline.": "Ou ouvrez un livre audio et choisissez Garder pour écouter hors ligne.",
    },
    de: {
      "That did not work. Try again.": "Das hat nicht geklappt. Versuch es noch einmal.",
      "The passkey was not used. Try again when you are ready.": "Der Passkey wurde nicht verwendet. Versuch es noch einmal, wenn du so weit bist.",
      "This device already has a passkey for this shelf.": "Dieses Gerät hat schon einen Passkey für dieses Shelf.",
      "Sending…": "Wird gesendet…",
      "Sent {0}%": "{0} % gesendet",
      "Sent. Shelf is putting it away…": "Gesendet. Shelf legt es ab…",
      "The upload stopped. Check the connection and try again.": "Das Hochladen ist abgebrochen. Prüfe die Verbindung und versuch es noch einmal.",
      "This book's file is no longer on this device.": "Die Datei dieses Buchs ist nicht mehr auf diesem Gerät.",
      "On this page": "Auf dieser Seite",
      "In this chapter": "In diesem Kapitel",
      "On this device; sent to the shelf when you are online.": "Auf diesem Gerät; wird an Shelf gesendet, wenn du online bist.",
      "Online again": "Wieder online",
      "Offline": "Offline",
      "Page {0} of {1}": "Seite {0} von {1}",
      "Chapter {0} of {1}": "Kapitel {0} von {1}",
      "Remove": "Entfernen",
      "Kept for reading offline": "Zum Offline-Lesen behalten",
      "The shelf cannot be reached right now. The books you kept on this device are here, and where you stop is sent back when you are online again.": "Shelf ist gerade nicht erreichbar. Hier sind die Bücher, die du auf diesem Gerät behalten hast, und deine Lesestelle wird gesendet, sobald du wieder online bist.",
      "Nothing kept on this device": "Nichts auf diesem Gerät behalten",
      "When you are online, open a book with an e-book and choose Keep for reading offline.": "Wenn du online bist, öffne ein Buch mit E-Book und wähle Zum Offline-Lesen behalten.",
      "Kept books": "Behaltene Bücher",
      "Previous": "Zurück",
      "Next": "Weiter",
      "Text size": "Schriftgröße",
      "Smaller": "Kleiner",
      "Default": "Normal",
      "Larger": "Größer",
      "Large": "Groß",
      "Largest": "Am größten",
      "Note, if you like": "Notiz, wenn du magst",
      "Highlight": "Markieren",
      "Cancel": "Abbrechen",
      "Highlights made here are kept on this device and sent to the shelf when you are online again.": "Markierungen, die du hier machst, bleiben auf diesem Gerät und werden an Shelf gesendet, sobald du wieder online bist.",
      "Scan the barcode": "Barcode scannen",
      "Hold the barcode on the back of the book inside the frame.": "Halte den Barcode auf der Rückseite des Buchs in den Rahmen.",
      "No ISBN barcode was found. Try again closer, with more light, or type the number in.": "Kein ISBN-Barcode gefunden. Versuch es näher, mit mehr Licht, oder tipp die Nummer ein.",
      "The camera could not be opened here. Press Scan the barcode again to take a photo of it instead.": "Die Kamera ließ sich hier nicht öffnen. Drück noch einmal auf Barcode scannen, um ein Foto davon zu machen.",
      "Reading the barcode…": "Barcode wird gelesen…",
      "That photo could not be read. Try again, or type the number in.": "Dieses Foto ließ sich nicht lesen. Versuch es noch einmal oder tipp die Nummer ein.",
      "Previous chapter": "Vorheriges Kapitel",
      "Back": "Zurück",
      "Forward": "Vorspulen",
      "Next chapter": "Nächstes Kapitel",
      "Slower": "Langsamer",
      "Faster": "Schneller",
      "Place in this chapter": "Stelle in diesem Kapitel",
      "Play": "Abspielen",
      "Pause": "Pause",
      "{0} left": "noch {0}",
      "Kept for listening": "Zum Hören gespeichert",
      "Or open an audiobook and choose Keep for listening offline.": "Oder öffne ein Hörbuch und wähle Zum Offline-Hören speichern.",
    },
  };

  const remembered = () => {
    try {
      return localStorage.getItem("shelf-language");
    } catch {
      return null;
    }
  };

  const fromPage = document.documentElement.lang;
  const language = (fromPage || remembered() || navigator.language || "en").slice(0, 2).toLowerCase();
  if (fromPage) {
    try {
      localStorage.setItem("shelf-language", fromPage);
    } catch {
      // A private window may refuse; the offline page then follows the browser.
    }
  }

  const table = tables[language] ?? {};
  window.t = (text, ...values) => (table[text] ?? text).replace(/\{(\d+)\}/g, (match, at) => (values[at] ?? match).toString());
  window.shelfLanguage = language;

  // Static pages mark their words with data-t, and attributes with data-t-placeholder and the like.
  window.translatePage = (root = document) => {
    for (const element of root.querySelectorAll("[data-t]")) {
      element.textContent = window.t(element.textContent.trim());
    }

    document.documentElement.lang = language;
  };
})();
