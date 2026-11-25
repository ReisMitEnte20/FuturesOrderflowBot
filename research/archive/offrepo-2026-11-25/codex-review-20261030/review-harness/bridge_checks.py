import sys,json,hashlib
from pathlib import Path
sys.path.insert(0,r'A:\Projects\FuturesOrderflowBot\research\data-bridge')
import atas_bridge as b
import pyarrow.parquet as pq
root=Path(__file__).parent
results={}
start='2026-10-26T08:00:00Z'; end='2026-10-26T08:01:00Z'
def fixture(name,lines):
 d=root/name; d.mkdir(); (d/'ticks.ndjson').write_text('\n'.join(lines)+'\n'); (d/'meta.json').write_text('{"instrument":"MNQ","tickSize":0.25}'); return d
def line(seq,tm,volume='1',price='100.25'):
 return '{"seq":'+str(seq)+',"dataType":"Trade","tMarket":"'+tm+'","tRecv":"2026-10-30T12:00:00.0000001Z","price":'+price+',"volume":'+volume+',"direction":"Buy"}'
d=fixture('precision',[line(1,'2026-10-26T08:00:00.0000001Z'),line(2,'2026-10-26T08:00:00.0000002Z'),line(3,'2026-10-26T08:00:00.0000002Z')])
m,t=b.convert(d,start,end,root/'precision_out')
results['precision']={'rows':t.num_rows,'market_ns':t['t_market_utc'].cast('int64').to_pylist(),'status_without_summary':m['qualityFlags']['status'],'clean':m['reconciliation']['summary_clean'],'ids':t['recorder_seq'].to_pylist()}
m2,t2=b.convert(d,'2026-10-26T08:00:00.0000002Z',end,root/'fractional_window')
results['fractional_boundary']={'expected_rows':2,'actual_rows':t2.num_rows}
d2=fixture('bad_json',[line(1,start),'{broken'])
m,t=b.convert(d2,start,end,root/'bad_json_out'); results['bad_json']={'status':m['qualityFlags']['status'],'bad':m['qualityFlags']['badJsonLines']}
d3=fixture('mutation',[line(1,start)])
original_hash=b.sha256_file
changed=False
def mutate_hash(path):
 global changed
 if Path(path)==d3/'ticks.ndjson' and not changed:
  with open(path,'a') as f: f.write(line(2,start)+'\n')
  changed=True
 return original_hash(path)
b.sha256_file=mutate_hash
m,t=b.convert(d3,start,end,root/'mutation_out')
b.sha256_file=original_hash
results['mutation']={'exported_rows':t.num_rows,'current_input_rows':sum(1 for _ in open(d3/'ticks.ndjson')),'manifest_hash_matches_changed_input':m['inputHashes']['ticks.ndjson']==original_hash(d3/'ticks.ndjson'),'status':m['qualityFlags']['status']}
# Existing target is reused; simulate failure while replacing parquet, only inside review directory.
old_manifest=(root/'mutation_out'/'manifest.json').read_bytes()
original_write=b.pq.write_table
def interrupted(table,path,**kwargs):
 Path(path).write_bytes(b'PARTIAL'); raise OSError('synthetic interrupted parquet write')
b.pq.write_table=interrupted
try: b.convert(d3,start,end,root/'mutation_out')
except OSError: pass
finally: b.pq.write_table=original_write
results['interruption']={'old_manifest_survives':(root/'mutation_out'/'manifest.json').read_bytes()==old_manifest,'parquet_partial':(root/'mutation_out'/'trades.parquet').read_bytes()==b'PARTIAL'}
real=Path(r'F:\ATAS_Data\Recordings\MNQ_20261029_232000Z')
before={p.name:original_hash(p) for p in real.iterdir() if p.is_file()}
m,t=b.convert(real,start,end,root/'real_conversion')
after={p.name:original_hash(p) for p in real.iterdir() if p.is_file()}
existing=pq.read_table(r'F:\ATAS_Data\Bridge\MNQ_20261029_232000Z_0800\trades.parquet')
results['real']={'total':m['coverage']['tradesTotalInFile'],'in_window':t.num_rows,'after':m['coverage']['restTicksAfter'],'volume':m['trades']['totalVolume'],'clean':m['reconciliation']['summary_clean'],'raw_hashes_unchanged':before==after,'logical_equals_delivered_parquet':t.equals(existing)}
(root/'bridge_checks.json').write_text(json.dumps(results,indent=2))
print(json.dumps(results,indent=2))
