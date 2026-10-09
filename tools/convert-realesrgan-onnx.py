#!/usr/bin/env python3
"""Converts Real-ESRGAN's compact models (SRVGGNetCompact) from their official PyTorch weights to
ONNX, for src/MyVideoEditor.Core/Upscaling/Models/. The .onnx files are committed, so this only
needs running to reproduce or update them.

    python3 -m venv /tmp/onnx-venv && /tmp/onnx-venv/bin/pip install torch onnx
    /tmp/onnx-venv/bin/python tools/convert-realesrgan-onnx.py

Real-ESRGAN and its models: BSD-3-Clause, https://github.com/xinntao/Real-ESRGAN (v0.2.5.0).
ArtCNN_C4F32.onnx is used as published by https://github.com/Artoriuz/ArtCNN (v1.6.2, MIT).
"""
import hashlib
import os
import urllib.request

import torch
import torch.nn as nn
import torch.nn.functional as F

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "MyVideoEditor.Core", "Upscaling", "Models")
RELEASE = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/"
# name: (number of body convolutions, SHA-256 of the official .pth)
MODELS = {
    "realesr-general-x4v3": (32, "8dc7edb9ac80ccdc30c3a5dca6616509367f05fbc184ad95b731f05bece96292"),
    "realesr-animevideov3": (16, "b8a8376811077954d82ca3fcf476f1ac3da3e8a68a4f4d71363008000a18b75d"),
}


class SRVGGNetCompact(nn.Module):
    """realesrgan/archs/srvgg_arch.py from Real-ESRGAN (BSD-3-Clause)."""

    def __init__(self, num_in_ch=3, num_out_ch=3, num_feat=64, num_conv=16, upscale=4):
        super().__init__()
        self.upscale = upscale
        self.body = nn.ModuleList([nn.Conv2d(num_in_ch, num_feat, 3, 1, 1), nn.PReLU(num_parameters=num_feat)])
        for _ in range(num_conv):
            self.body.append(nn.Conv2d(num_feat, num_feat, 3, 1, 1))
            self.body.append(nn.PReLU(num_parameters=num_feat))
        self.body.append(nn.Conv2d(num_feat, num_out_ch * upscale * upscale, 3, 1, 1))
        self.upsampler = nn.PixelShuffle(upscale)

    def forward(self, x):
        out = x
        for layer in self.body:
            out = layer(out)
        return self.upsampler(out) + F.interpolate(x, scale_factor=self.upscale, mode="nearest")


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, (num_conv, sha) in MODELS.items():
        data = urllib.request.urlopen(RELEASE + name + ".pth").read()
        if hashlib.sha256(data).hexdigest() != sha:
            raise SystemExit(f"{name}.pth doesn't match its expected checksum")
        path = os.path.join(OUT, name + ".pth.tmp")
        with open(path, "wb") as f:
            f.write(data)
        state = torch.load(path, map_location="cpu", weights_only=True)
        os.remove(path)
        net = SRVGGNetCompact(num_conv=num_conv)
        net.load_state_dict(state.get("params_ema", state.get("params", state)), strict=True)
        net.eval()
        # Input: RGB, 0..1, NCHW; output 4x.
        torch.onnx.export(net, torch.rand(1, 3, 64, 64), os.path.join(OUT, name + ".onnx"),
                          input_names=["input"], output_names=["output"], opset_version=17,
                          dynamic_axes={"input": {2: "H", 3: "W"}, "output": {2: "H4", 3: "W4"}}, dynamo=False)
        print("wrote", name + ".onnx")


if __name__ == "__main__":
    main()
