# Huaci offline OCR assets

Huaci uses RapidOcrNet 2.0.0 with the PP-OCRv5 mobile pipeline. The detector
and orientation classifier are supplied by the RapidOcrNet NuGet package in
`models/v5/` beside the executable. Huaci supplies this matching Chinese
recognizer and dictionary in `Ocr/models/v5/`:

- `ch_PP-OCRv5_rec_mobile.onnx` — SHA-256
  `5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5`
- `ppocrv5_dict.txt` — SHA-256
  `d1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b`

The recognizer contains Chinese, Latin letters, digits and punctuation, so it
is used for both Chinese and English screenshots. The recognizer and its
dictionary are a required pair; replacing only one produces invalid output.

Official upstream sources:

- Model: https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.1/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx
- Dictionary: https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.1/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt
- Model manifest: https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml

These assets and the OCR library are used locally. Screenshot pixels are
passed to the engine in memory and are not uploaded or written to a temporary
image file.
