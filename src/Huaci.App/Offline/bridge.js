import { LatencyOptimisedTranslator } from "/engine/translator.js";

const translator = new LatencyOptimisedTranslator({
  registryUrl: "/models/index.json",
  pivotLanguage: null,
  downloadTimeout: 0,
  cacheSize: 64,
  useNativeIntGemm: false
});

const reply = message => window.chrome.webview.postMessage(message);

window.chrome.webview.addEventListener("message", async event => {
  const request = event.data;
  if (!request || request.type !== "translate" || typeof request.id !== "string") {
    return;
  }

  try {
    const text = typeof request.text === "string" ? request.text.trim() : "";
    if (!text) {
      throw new Error("没有可翻译的文本。");
    }

    const response = await translator.translate({
      from: "en",
      to: "zh",
      text,
      html: false,
      qualityScores: false
    });

    reply({
      type: "translation-result",
      id: request.id,
      ok: true,
      text: response.target.text
    });
  } catch (error) {
    reply({
      type: "translation-result",
      id: request.id,
      ok: false,
      error: error?.message || String(error)
    });
  }
});

window.addEventListener("unhandledrejection", event => {
  reply({
    type: "engine-error",
    error: event.reason?.message || String(event.reason)
  });
});

reply({ type: "ready" });
