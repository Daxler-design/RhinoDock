import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import type { Port, Action } from './types';

export function ChoiceInput({ port, act }: { port: Port; act: (action: Action) => void }) {
  const choices = port.choices ?? [];
  const selected = port.value == null ? '@default' : choices.find(c => c.value === port.value)?.key ?? '@unknown';
  const defaultLabel = choices.find(c => c.value === port.defaultValue)?.label ?? port.defaultValue ?? 'Optional';
  return <Select value={selected} onValueChange={key => key === '@default'
    ? act({ action: 'input', name: port.name, value: null })
    : act({ action: 'input', name: port.name, value: key })}>
    <SelectTrigger className="w-full min-w-0" aria-label={port.name}><SelectValue/></SelectTrigger>
    <SelectContent position="popper"><SelectGroup>
      {port.optional && <SelectItem value="@default">{defaultLabel} (default)</SelectItem>}
      {!port.optional && selected === '@default' && <SelectItem value="@default" disabled>Required</SelectItem>}
      {selected === '@unknown' && <SelectItem value="@unknown" disabled>{port.value} (current)</SelectItem>}
      {choices.map(c => <SelectItem key={c.key} value={c.key}>{c.label}</SelectItem>)}
    </SelectGroup></SelectContent>
  </Select>;
}
