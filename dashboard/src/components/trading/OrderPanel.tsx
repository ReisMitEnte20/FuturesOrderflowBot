import { useState } from "react";
import { Button } from "@/components/common/Button";
import { Badge } from "@/components/common/Badge";
import { Card, CardBody, CardHeader } from "@/components/common/Card";
import { useTradingStore } from "@/stores/tradingStore";
import { Order } from "@/types";
import { ArrowRightLeft, AlertTriangle, CheckCircle, XCircle, Clock, Loader2 } from "lucide-react";

interface OrderPanelProps {
  symbol?: string;
  compact?: boolean;
}

export function OrderPanel({ symbol: propSymbol, compact }: OrderPanelProps) {
  const [side, setSide] = useState<"buy" | "sell">("buy");
  const [type, setType] = useState<"market" | "limit" | "stop">("market");
  const [quantity, setQuantity] = useState("1");
  const [price, setPrice] = useState("");
  const [stopPrice, setStopPrice] = useState("");
  const selectedSymbol = useTradingStore((s) => s.selectedSymbol);
  const addOrder = useTradingStore((s) => s.addOrder);

  const sym = propSymbol || selectedSymbol;

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!quantity || Number(quantity) <= 0) return;

    addOrder({
      id: `ord-${Date.now()}`,
      symbol: sym,
      side,
      type,
      quantity: Number(quantity),
      price: price ? Number(price) : undefined,
      stopPrice: stopPrice ? Number(stopPrice) : undefined,
      status: "pending",
      timestamp: new Date().toISOString(),
    });

    setQuantity("1");
    setPrice("");
    setStopPrice("");
  };

  if (compact) {
    return (
      <Card className="p-3">
        <form onSubmit={handleSubmit} className="flex items-center gap-2">
          <Button
            variant={side === "buy" ? "primary" : "danger"}
            size="sm"
            onClick={() => setSide("buy")}
          >
            BUY
          </Button>
          <Button
            variant={side === "sell" ? "danger" : "default"}
            size="sm"
            onClick={() => setSide("sell")}
          >
            SELL
          </Button>
          <input
            type="number"
            value={quantity}
            onChange={(e) => setQuantity(e.target.value)}
            placeholder="Qty"
            className="w-16 bg-[var(--bg)] border border-[var(--line)] rounded px-2 py-1 text-sm font-mono text-[var(--fg)] focus:border-[var(--key)] focus:outline-none"
          />
          <Button variant="primary" size="sm" type="submit">
            <ArrowRightLeft className="h-3 w-3" />
          </Button>
        </form>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader className="flex items-center justify-between">
        <h3 className="font-mono text-sm font-medium">Order Entry</h3>
        <Badge variant="info">{sym}</Badge>
      </CardHeader>
      <CardBody>
        <form onSubmit={handleSubmit} className="space-y-3">
          <div className="grid grid-cols-2 gap-2">
            <Button
              variant={side === "buy" ? "primary" : "default"}
              type="button"
              onClick={() => setSide("buy")}
              className={side === "buy" ? "bg-[var(--key)]" : ""}
            >
              BUY
            </Button>
            <Button
              variant={side === "sell" ? "danger" : "default"}
              type="button"
              onClick={() => setSide("sell")}
            >
              SELL
            </Button>
          </div>

          <div className="flex gap-1">
            {(["market", "limit", "stop"] as const).map((t) => (
              <button
                key={t}
                type="button"
                onClick={() => setType(t)}
                className={`flex-1 py-1 rounded text-xs font-mono border transition-colors ${
                  type === t
                    ? "bg-[var(--key)]/20 text-[var(--key)] border-[var(--key)]/40"
                    : "bg-[var(--bg)] border-[var(--line)] text-[var(--fg-faint)] hover:bg-[var(--panel-2)]"
                }`}
              >
                {t}
              </button>
            ))}
          </div>

          <div className="grid grid-cols-2 gap-2">
            <input
              type="number"
              value={quantity}
              onChange={(e) => setQuantity(e.target.value)}
              placeholder="Quantity"
              className="bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] placeholder:text-[var(--fg-faint)] focus:border-[var(--key)] focus:outline-none"
              required
            />
            {type === "limit" && (
              <input
                type="number"
                value={price}
                onChange={(e) => setPrice(e.target.value)}
                placeholder="Price"
                className="bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] placeholder:text-[var(--fg-faint)] focus:border-[var(--key)] focus:outline-none"
                required
              />
            )}
            {type === "stop" && (
              <input
                type="number"
                value={stopPrice}
                onChange={(e) => setStopPrice(e.target.value)}
                placeholder="Stop price"
                className="bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] placeholder:text-[var(--fg-faint)] focus:border-[var(--key)] focus:outline-none"
                required
              />
            )}
            {type !== "limit" && type !== "stop" && <div />}
          </div>

          <Button variant="primary" type="submit" className="w-full">
            {side.toUpperCase()} {sym}
          </Button>
        </form>
      </CardBody>
    </Card>
  );
}

export function OrderStatus({ orders }: { orders: Order[] }) {
  if (!orders.length) return null;

  return (
    <div className="space-y-2">
      {orders.slice(0, 10).map((order) => (
        <div key={order.id} className="flex items-center justify-between p-2 rounded border border-[var(--line)] bg-[var(--bg-2)]">
          <div className="flex items-center gap-2">
            <Badge variant={order.side === "buy" ? "success" : "danger"}>
              {order.side.toUpperCase()}
            </Badge>
            <span className="font-mono text-xs">{order.symbol}</span>
            <span className="text-[10px] text-[var(--fg-faint)] font-mono">{order.type}</span>
          </div>
          {orderStatusIcon(order.status)}
          <span className="font-mono text-xs text-[var(--fg-dim)]">
            {order.filledQuantity ? `${order.filledQuantity}/${order.quantity}` : order.quantity}
          </span>
        </div>
      ))}
    </div>
  );
}

function orderStatusIcon(status: Order["status"]) {
  switch (status) {
    case "filled":
      return <CheckCircle className="h-3.5 w-3.5 text-[var(--key)]" />;
    case "pending":
      return <Clock className="h-3.5 w-3.5 text-[var(--gold)]" />;
    case "cancelled":
      return <XCircle className="h-3.5 w-3.5 text-[var(--fg-faint)]" />;
    case "rejected":
      return <AlertTriangle className="h-3.5 w-3.5 text-[var(--red)]" />;
    default:
      return <Loader2 className="h-3.5 w-3.5 animate-spin text-[var(--fg-faint)]" />;
  }
}