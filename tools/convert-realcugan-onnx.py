#!/usr/bin/env python3
"""Converts Real-CUGAN's conservative models (2x, 3x, 4x) from their official PyTorch weights to
ONNX, for src/MyVideoEditor.Core/Upscaling/Models/. The .onnx files are committed, so this only
needs running to reproduce or update them.

The network is upcunet_v3.py's untiled path (tile_mode 0), output as floats (0..1) rather than
bytes. Weights are stored at half precision with a Cast to full precision in front of each (about
2.6 MB per model instead of 5.2 MB); the output differs by at most one 8-bit level. Checked against
the official PyTorch code: identical apart from rounding (at most one level).

Input sides must be a multiple of 2 (4 for 3x) and over 40 pixels (the model pads by reflection).
The squeeze-and-excitation blocks average over the whole input, so a tiled frame differs very
slightly from a whole one (about 50 dB PSNR with 256-pixel tiles; the ncnn program tiles too).

    python3 -m venv /tmp/onnx-venv && /tmp/onnx-venv/bin/pip install torch onnx
    /tmp/onnx-venv/bin/python tools/convert-realcugan-onnx.py

Real-CUGAN and its models: MIT, https://github.com/bilibili/ailab/tree/main/Real-CUGAN
(the "updated_weights" from the Real-CUGAN release).
"""
import hashlib
import io
import os
import urllib.request
import zipfile

import numpy as np
import onnx
import torch
import torch.nn as nn
import torch.nn.functional as F
from onnx import TensorProto, helper, numpy_helper

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "MyVideoEditor.Core", "Upscaling", "Models")
WEIGHTS_URL = "https://github.com/bilibili/ailab/releases/download/Real-CUGAN/updated_weights.zip"
WEIGHTS_SHA = "99a8ce2e4a6ce0548668879f148b3db3e143201d3df42c825a4143490396da53"
# scale: SHA-256 of updated_weights/up{scale}x-latest-conservative.pth
MODELS = {
    2: "6cfe3b23687915d08ba96010f25198d9cfe8a683aa4131f1acf7eaa58ee1de93",
    3: "f6ea5fd20380413beb2701182483fd80c2e86f3b3f08053eb3df4975184aefe3",
    4: "a8c8185def699b0883662a02df0ef2e6db3b0275170b6cc0d28089b64b273427",
}


class SEBlock(nn.Module):
    def __init__(self, channels, reduction=8):
        super().__init__()
        self.conv1 = nn.Conv2d(channels, channels // reduction, 1, 1, 0)
        self.conv2 = nn.Conv2d(channels // reduction, channels, 1, 1, 0)

    def forward(self, x):
        x0 = torch.mean(x, dim=(2, 3), keepdim=True)
        return x * torch.sigmoid(self.conv2(F.relu(self.conv1(x0))))


class UNetConv(nn.Module):
    def __init__(self, in_channels, mid_channels, out_channels, se):
        super().__init__()
        self.conv = nn.Sequential(nn.Conv2d(in_channels, mid_channels, 3, 1, 0), nn.LeakyReLU(0.1),
                                  nn.Conv2d(mid_channels, out_channels, 3, 1, 0), nn.LeakyReLU(0.1))
        self.seblock = SEBlock(out_channels) if se else None

    def forward(self, x):
        z = self.conv(x)
        return self.seblock(z) if self.seblock is not None else z


def crop(x, n):
    return x[:, :, n:-n, n:-n]


class UNet1(nn.Module):
    def __init__(self, in_channels, out_channels, x3=False):
        super().__init__()
        self.conv1 = UNetConv(in_channels, 32, 64, se=False)
        self.conv1_down = nn.Conv2d(64, 64, 2, 2, 0)
        self.conv2 = UNetConv(64, 128, 64, se=True)
        self.conv2_up = nn.ConvTranspose2d(64, 64, 2, 2, 0)
        self.conv3 = nn.Conv2d(64, 64, 3, 1, 0)
        self.conv_bottom = nn.ConvTranspose2d(64, out_channels, 5, 3, 2) if x3 else nn.ConvTranspose2d(64, out_channels, 4, 2, 3)

    def forward(self, x):
        x1 = self.conv1(x)
        x2 = F.leaky_relu(self.conv1_down(x1), 0.1)
        x2 = F.leaky_relu(self.conv2_up(self.conv2(x2)), 0.1)
        return self.conv_bottom(F.leaky_relu(self.conv3(crop(x1, 4) + x2), 0.1))


class UNet2(nn.Module):
    def __init__(self, in_channels, out_channels):
        super().__init__()
        self.conv1 = UNetConv(in_channels, 32, 64, se=False)
        self.conv1_down = nn.Conv2d(64, 64, 2, 2, 0)
        self.conv2 = UNetConv(64, 64, 128, se=True)
        self.conv2_down = nn.Conv2d(128, 128, 2, 2, 0)
        self.conv3 = UNetConv(128, 256, 128, se=True)
        self.conv3_up = nn.ConvTranspose2d(128, 128, 2, 2, 0)
        self.conv4 = UNetConv(128, 64, 64, se=True)
        self.conv4_up = nn.ConvTranspose2d(64, 64, 2, 2, 0)
        self.conv5 = nn.Conv2d(64, 64, 3, 1, 0)
        self.conv_bottom = nn.Conv2d(64, out_channels, 3, 1, 0)

    def forward(self, x):
        x1 = self.conv1(x)
        x2 = self.conv2(F.leaky_relu(self.conv1_down(x1), 0.1))
        x3 = F.leaky_relu(self.conv2_down(x2), 0.1)
        x3 = F.leaky_relu(self.conv3_up(self.conv3(x3)), 0.1)
        x4 = F.leaky_relu(self.conv4_up(self.conv4(crop(x2, 4) + x3)), 0.1)
        return self.conv_bottom(F.leaky_relu(self.conv5(crop(x1, 16) + x4), 0.1))


class UpCunet(nn.Module):
    """UpCunet2x/3x/4x from upcunet_v3.py (bilibili/ailab, MIT), untiled. Input and output: RGB, 0..1, NCHW."""

    def __init__(self, scale):
        super().__init__()
        self.scale = scale
        if scale == 4:
            self.unet1 = UNet1(3, 64)
            self.unet2 = UNet2(64, 64)
            self.ps = nn.PixelShuffle(2)
            self.conv_final = nn.Conv2d(64, 12, 3, 1, 0)
        else:
            self.unet1 = UNet1(3, 3, x3=scale == 3)
            self.unet2 = UNet2(3, 3)

    def forward(self, x):
        pad = {2: 18, 3: 14, 4: 19}[self.scale]
        y = self.unet1(F.pad(x, (pad, pad, pad, pad), "reflect"))
        y = self.unet2(y) + crop(y, 20)
        if self.scale == 4:
            y = self.ps(crop(self.conv_final(y), 1)) + F.interpolate(x, scale_factor=4, mode="nearest")
        return y


def store_weights_as_half(path):
    """Weights as float16, each followed by a Cast back to float32 (ONNX Runtime folds these at load)."""
    model = onnx.load(path)
    graph = model.graph
    initializers, casts = [], []
    for init in graph.initializer:
        array = numpy_helper.to_array(init)
        if array.dtype == np.float32 and array.size > 16:
            half = numpy_helper.from_array(array.astype(np.float16), init.name + "_fp16")
            initializers.append(half)
            casts.append(helper.make_node("Cast", [half.name], [init.name], to=TensorProto.FLOAT))
        else:
            initializers.append(init)
    del graph.initializer[:]
    graph.initializer.extend(initializers)
    nodes = list(graph.node)
    del graph.node[:]
    graph.node.extend(casts + nodes)
    onnx.checker.check_model(model)
    onnx.save(model, path)


def main():
    os.makedirs(OUT, exist_ok=True)
    archive = urllib.request.urlopen(WEIGHTS_URL).read()
    if hashlib.sha256(archive).hexdigest() != WEIGHTS_SHA:
        raise SystemExit(f"{WEIGHTS_URL} doesn't match its expected checksum")
    with zipfile.ZipFile(io.BytesIO(archive)) as z:
        for scale, sha in MODELS.items():
            data = z.read(f"updated_weights/up{scale}x-latest-conservative.pth")
            if hashlib.sha256(data).hexdigest() != sha:
                raise SystemExit(f"up{scale}x-latest-conservative.pth doesn't match its expected checksum")
            state = torch.load(io.BytesIO(data), map_location="cpu", weights_only=True)
            state.pop("pro", None)
            net = UpCunet(scale)
            net.load_state_dict(state, strict=True)
            net.eval()
            path = os.path.join(OUT, f"realcugan-x{scale}.onnx")
            torch.onnx.export(net, torch.rand(1, 3, 64, 64), path, input_names=["input"], output_names=["output"],
                              opset_version=17, dynamic_axes={"input": {2: "H", 3: "W"}, "output": {2: "OH", 3: "OW"}}, dynamo=False)
            store_weights_as_half(path)
            print("wrote", os.path.basename(path))


if __name__ == "__main__":
    main()
