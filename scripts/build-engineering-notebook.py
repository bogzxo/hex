"""Generate the deterministic notebook used by Hex's engineering integration tests."""
import json
from pathlib import Path
from textwrap import dedent

cells = []

def cell(kind, source):
    item = {"cell_type": kind, "id": f"eng-{len(cells):04}", "metadata": {},
            "source": dedent(source).strip().splitlines(keepends=True)}
    if kind == "code":
        item.update(execution_count=None, outputs=[])
    cells.append(item)

cell("markdown", r"""
# Engineering calculations

All quantities use SI units. Each calculation checks an independent reference answer.

Inline math: beam stress $\sigma=My/I$ and natural frequency \(\omega_n=\sqrt{k/m}\).

$$\delta=\frac{FL^3}{3EI}$$

\[\mathbf{K}\mathbf{u}=\mathbf{f}\]

```python
# Dollar signs in code stay literal: "$not_math$"
```
""")

cell("code", """
import math
import io
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from IPython.display import display, Latex, SVG, HTML, Image, update_display
results = {}
def check(name, actual, expected, unit, rel=1e-9):
    assert math.isclose(float(actual), expected, rel_tol=rel, abs_tol=1e-12), (name, actual, expected)
    results[name] = (float(actual), unit)
    print(f'{name}: {actual:.9g} {unit} [PASS]')
""")

cell("code", r"""
F, L, E, I = 1000.0, 2.0, 200e9, 8e-6
delta = F * L**3 / (3 * E * I)
check('Cantilever tip deflection', delta, 1/600, 'm')
display(Latex(r'\delta=\frac{FL^3}{3EI}=1.666667\times10^{-3}\,\mathrm{m}'))
""")

cell("code", r"""
R, C, V0, t = 1000.0, 100e-6, 5.0, 0.3
voltage = V0 * (1 - math.exp(-t/(R*C)))
check('RC charge at three time constants', voltage, 4.75106465816068, 'V')
display(Latex(r'V(t)=V_0\left(1-e^{-t/(RC)}\right)'))
""")

cell("code", r"""
mu, length, flow, diameter = 0.001, 10.0, 1e-5, 0.01
pressure = 128 * mu * length * flow / (math.pi * diameter**4)
check('Laminar pipe pressure drop', pressure, 407.4366543152521, 'Pa')
reynolds = 4 * 1000 * flow / (math.pi * mu * diameter)
assert reynolds < 2300, 'Poiseuille assumption requires laminar flow'
display(Latex(r'\Delta p=\frac{128\mu LQ}{\pi D^4},\quad Re<2300'))
""")

cell("code", r"""
k, area, dT, thickness = 205.0, 0.01, 80.0, 0.02
heat = k * area * dT / thickness
check('Steady wall conduction', heat, 8200.0, 'W')
display(Latex(r'\dot Q=\frac{kA\Delta T}{L}=8200\,\mathrm{W}'))
""")

cell("code", r"""
matrix = np.array([[2., 1.], [1., 3.]])
loads = np.array([5., 7.])
solution = np.linalg.solve(matrix, loads)
check('Matrix displacement x', solution[0], 1.6, 'm')
check('Matrix displacement y', solution[1], 1.8, 'm')
assert np.allclose(matrix @ solution, loads, rtol=0, atol=1e-12)
display(Latex(r'\begin{bmatrix}2&1\\1&3\end{bmatrix}\begin{bmatrix}x\\y\end{bmatrix}=\begin{bmatrix}5\\7\end{bmatrix}'))
xs = np.linspace(0, 2, 10001)
integral = np.trapezoid(xs**2, xs)
check('Integrated distributed load', integral, 8/3, 'N', rel=1e-7)
display(Latex(r'\int_0^2 x^2\,dx=\frac83'))
""")

cell("code", r"""
# Real Matplotlib SVG: definitions, reusable glyph paths, clipping, transforms, and axes.
xs = np.linspace(0, L, 100)
ys = F * xs**2 * (3*L-xs)/(6*E*I)
fig, ax = plt.subplots(figsize=(6, 3), layout='constrained')
ax.plot(xs, ys * 1000, label='Cantilever')
ax.set(xlabel='Position [m]', ylabel='Deflection [mm]', title='Engineering beam response')
ax.grid(True)
ax.legend()
svg = io.StringIO()
fig.savefig(svg, format='svg')
display(SVG(svg.getvalue()))
png = io.BytesIO()
fig.savefig(png, format='png', dpi=100)
display(Image(data=png.getvalue()))
plt.close(fig)
assert '<clipPath' in svg.getvalue() and '<path' in svg.getvalue()
print('Matplotlib SVG and PNG [PASS]')
""")

cell("code", r"""
# One display ID updated after its original output.
display(Latex(r'F=0\,\mathrm{N}'), display_id='engineering-load')
update_display(Latex(r'F=1000\,\mathrm{N}'), display_id='engineering-load')
rows = ''.join(f'<tr><td>{name}</td><td>{value:.9g}</td><td>{unit}</td></tr>'
               for name, (value, unit) in results.items())
display(HTML('<table><thead><tr><th>Calculation</th><th>Value</th><th>Unit</th></tr></thead><tbody>' + rows + '</tbody></table>'))
assert len(results) == 7
print('ENGINEERING_SUITE_PASS: 7 numerical reference checks')
""")

notebook = {"cells": cells, "metadata": {"kernelspec": {
    "display_name": "Python 3 (ipykernel)", "language": "python", "name": "python3"},
    "language_info": {"name": "python"}}, "nbformat": 4, "nbformat_minor": 5}
target = Path(__file__).resolve().parents[1] / "samples" / "notebooks" / "engineering.ipynb"
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(json.dumps(notebook, indent=1, sort_keys=True) + '\n', encoding='utf-8')
print(target)
