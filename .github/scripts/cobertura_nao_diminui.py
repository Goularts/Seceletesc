"""Falha se docs/desenvolvimento/cobertura-obrigatoria.txt perdeu regras ou teve mínimos reduzidos
em relação à branch de destino. Uso: python cobertura_nao_diminui.py <arquivo_base> <arquivo_atual>"""
import sys


def ler(caminho):
    regras = {}
    with open(caminho, encoding="utf-8") as f:
        for linha in f:
            linha = linha.split("#", 1)[0].strip()
            if linha:
                rid, minimo = linha.split()[:2]
                regras[rid] = int(minimo)
    return regras


base, atual = ler(sys.argv[1]), ler(sys.argv[2])
problemas = [f"{r}: removida" for r in base if r not in atual]
problemas += [f"{r}: mínimo caiu de {base[r]} para {atual[r]}" for r in base if r in atual and atual[r] < base[r]]

if problemas:
    print("A proteção das regras não pode diminuir:")
    for p in problemas:
        print(f"  - {p}")
    sys.exit(1)

print(f"Cobertura obrigatória preservada ({len(atual)} regras).")
