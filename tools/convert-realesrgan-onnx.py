#!/usr/bin/env python3
"""Converts Real-ESRGAN models from their official PyTorch weights to ONNX, for
src/MyVideoEditor.Core/Upscaling/Models/: the compact models (SRVGGNetCompact) and x4plus
(RRDBNet). The .onnx files are committed, so this only needs running to reproduce or update them.

x4plus is large, so its weights are stored at half precision with a Cast to full precision in
front of each (33.7 MB instead of 67 MB); the computation is unchanged and the output differs by
under one 8-bit level.

    python3 -m venv /tmp/onnx-venv && /tmp/onnx-venv/bin/pip install torch onnx
    /tmp/onnx-venv/bin/python tools/convert-realesrgan-onnx.py

Real-ESRGAN and its models: BSD-3-Clause, https://github.com/xinntao/Real-ESRGAN (v0.2.5.0, and
v0.1.0 for x4plus). The RRDBNet architecture is from BasicSR (Apache-2.0), https://github.com/XPixelGroup/BasicSR.
ArtCNN_C4F32.onnx is used as published by https://github.com/Artoriuz/ArtCNN (v1.6.2, MIT).
"""
import hashlib
import os
import urllib.request

import numpy as np
import onnx
import torch
import torch.nn as nn
import torch.nn.functional as F
from onnx import TensorProto, helper, numpy_helper

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "MyVideoEditor.Core", "Upscaling", "Models")
RELEASE = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/"
# name: (number of body convolutions, SHA-256 of the official .pth)
MODELS = {
    "realesr-general-x4v3": (32, "8dc7edb9ac80ccdc30c3a5dca6616509367f05fbc184ad95b731f05bece96292"),
    "realesr-animevideov3": (16, "b8a8376811077954d82ca3fcf476f1ac3da3e8a68a4f4d71363008000a18b75d"),
}
X4PLUS_URL = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth"
X4PLUS_SHA = "4fa0d38905f75ac06eb49a7951b426670021be3018265fd191d2125df9d682f1"


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


class RDB(nn.Module):
    """ResidualDenseBlock from BasicSR (Apache-2.0)."""

    def __init__(self, nf=64, gc=32):
        super().__init__()
        self.conv1 = nn.Conv2d(nf, gc, 3, 1, 1)
        self.conv2 = nn.Conv2d(nf + gc, gc, 3, 1, 1)
        self.conv3 = nn.Conv2d(nf + 2 * gc, gc, 3, 1, 1)
        self.conv4 = nn.Conv2d(nf + 3 * gc, gc, 3, 1, 1)
        self.conv5 = nn.Conv2d(nf + 4 * gc, nf, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(0.2, True)

    def forward(self, x):
        x1 = self.lrelu(self.conv1(x))
        x2 = self.lrelu(self.conv2(torch.cat((x, x1), 1)))
        x3 = self.lrelu(self.conv3(torch.cat((x, x1, x2), 1)))
        x4 = self.lrelu(self.conv4(torch.cat((x, x1, x2, x3), 1)))
        return self.conv5(torch.cat((x, x1, x2, x3, x4), 1)) * 0.2 + x


class RRDB(nn.Module):
    def __init__(self, nf, gc=32):
        super().__init__()
        self.rdb1, self.rdb2, self.rdb3 = RDB(nf, gc), RDB(nf, gc), RDB(nf, gc)

    def forward(self, x):
        return self.rdb3(self.rdb2(self.rdb1(x))) * 0.2 + x


class RRDBNet(nn.Module):
    """basicsr/archs/rrdbnet_arch.py (Apache-2.0), as used by Real-ESRGAN x4plus."""

    def __init__(self, nf=64, nb=23, gc=32):
        super().__init__()
        self.conv_first = nn.Conv2d(3, nf, 3, 1, 1)
        self.body = nn.Sequential(*[RRDB(nf, gc) for _ in range(nb)])
        self.conv_body = nn.Conv2d(nf, nf, 3, 1, 1)
        self.conv_up1 = nn.Conv2d(nf, nf, 3, 1, 1)
        self.conv_up2 = nn.Conv2d(nf, nf, 3, 1, 1)
        self.conv_hr = nn.Conv2d(nf, nf, 3, 1, 1)
        self.conv_last = nn.Conv2d(nf, 3, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(0.2, True)

    def forward(self, x):
        feat = self.conv_first(x)
        feat = feat + self.conv_body(self.body(feat))
        feat = self.lrelu(self.conv_up1(F.interpolate(feat, scale_factor=2, mode="nearest")))
        feat = self.lrelu(self.conv_up2(F.interpolate(feat, scale_factor=2, mode="nearest")))
        return self.conv_last(self.lrelu(self.conv_hr(feat)))


def fetch(url, sha):
    data = urllib.request.urlopen(url).read()
    if hashlib.sha256(data).hexdigest() != sha:
        raise SystemExit(f"{url} doesn't match its expected checksum")
    path = os.path.join(OUT, "download.pth.tmp")
    with open(path, "wb") as f:
        f.write(data)
    state = torch.load(path, map_location="cpu", weights_only=True)
    os.remove(path)
    return state.get("params_ema", state.get("params", state))


def export(net, name):
    net.eval()
    # Input: RGB, 0..1, NCHW; output 4x.
    torch.onnx.export(net, torch.rand(1, 3, 64, 64), os.path.join(OUT, name + ".onnx"),
                      input_names=["input"], output_names=["output"], opset_version=17,
                      dynamic_axes={"input": {2: "H", 3: "W"}, "output": {2: "H4", 3: "W4"}}, dynamo=False)


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
    for name, (num_conv, sha) in MODELS.items():
        net = SRVGGNetCompact(num_conv=num_conv)
        net.load_state_dict(fetch(RELEASE + name + ".pth", sha), strict=True)
        export(net, name)
        print("wrote", name + ".onnx")
    net = RRDBNet()
    net.load_state_dict(fetch(X4PLUS_URL, X4PLUS_SHA), strict=True)
    export(net, "realesrgan-x4plus")
    store_weights_as_half(os.path.join(OUT, "realesrgan-x4plus.onnx"))
    print("wrote realesrgan-x4plus.onnx")


if __name__ == "__main__":
    main()
