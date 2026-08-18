import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  FormControl,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { apiClient } from '../api/client'

function asError(error: unknown): Error {
  if (error instanceof Error) return error
  if (typeof error === 'object' && error !== null && 'detail' in error) return new Error(String((error as { detail?: unknown }).detail ?? 'OGFI request failed'))
  return new Error('OGFI request failed')
}

async function requireResult<T>(promise: Promise<{ data?: T; error?: unknown }>): Promise<T> {
  const result = await promise
  if (result.error) throw asError(result.error)
  if (result.data === undefined) throw new Error('OGFI returned no response body.')
  return result.data
}

function SectionHeader({ title, description }: { title: string; description: string }) {
  return <Box sx={{ mb: 2 }}><Typography variant="h4" sx={{ fontWeight: 700 }}>{title}</Typography><Typography color="text.secondary">{description}</Typography></Box>
}

function LoadingOrError({ pending, error }: { pending: boolean; error: Error | null }) {
  if (pending) return <Alert severity="info">Loading authoritative server state…</Alert>
  if (error) return <Alert severity="error">{error.message}</Alert>
  return null
}

export function BatchEOverviewPage() {
  const cards = [
    ['Goods Receipt', 'Procurement-owned DRAFT → POSTED receipt with approved-PO eligibility, partial receipt and zero over-receipt tolerance.'],
    ['Posted Fact', 'One versioned GoodsReceiptPosted.v1 outbox fact carries immutable receipt, UOM, conversion and commercial context.'],
    ['Inventory Movement', 'Append-only Purchase Receipt movement is the authoritative normalized stock consequence.'],
    ['Stock Position', 'Read-optimized balance is derived from the movement ledger and can be rebuilt without fake adjustments.'],
  ]
  return <>
    <SectionHeader title="Batch E · Stock Consequence" description="RI01-BL05 candidate workspace. Batch E remains unaccepted until controlled G9.6 validation and explicit RJ approval." />
    <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2} sx={{ flexWrap: 'wrap' }}>
      {cards.map(([title, body]) => <Card key={title} variant="outlined" sx={{ flex: '1 1 280px' }}><CardContent><Typography variant="h6" gutterBottom>{title}</Typography><Typography variant="body2" color="text.secondary">{body}</Typography></CardContent></Card>)}
    </Stack>
    <Alert severity="info" sx={{ mt: 3 }}>Tenant, authorization, UOM normalization, idempotency and stock truth remain server-authoritative. Finance posting is intentionally outside Batch E.</Alert>
  </>
}

export function GoodsReceiptsPage() {
  const queryClient = useQueryClient()
  const [purchaseOrderId, setPurchaseOrderId] = useState('')
  const [stockLocationId, setStockLocationId] = useState('')
  const [quantity, setQuantity] = useState('1')
  const receipts = useQuery({ queryKey: ['goods-receipts'], queryFn: () => requireResult(apiClient.GET('/api/procurement/goods-receipts', { params: { query: { limit: 100 } } })) })
  const purchaseOrders = useQuery({ queryKey: ['approved-purchase-orders'], queryFn: () => requireResult(apiClient.GET('/api/procurement/purchase-orders', { params: { query: { status: 'APPROVED', limit: 100 } } })) })
  const locations = useQuery({ queryKey: ['stock-locations'], queryFn: () => requireResult(apiClient.GET('/api/inventory/stock-locations', { params: { query: { limit: 100 } } })) })
  const selectedPo = useQuery({
    queryKey: ['purchase-order', purchaseOrderId], enabled: !!purchaseOrderId,
    queryFn: () => requireResult(apiClient.GET('/api/procurement/purchase-orders/{purchaseOrderId}', { params: { path: { purchaseOrderId } } })),
  })
  const create = useMutation({
    mutationFn: async () => {
      const line = selectedPo.data?.lines[0]
      if (!purchaseOrderId || !stockLocationId || !line) throw new Error('Select an approved Purchase Order and receiving Stock Location.')
      const parsed = Number(quantity)
      if (!Number.isFinite(parsed) || parsed <= 0) throw new Error('Received quantity must be greater than zero.')
      return requireResult(apiClient.POST('/api/procurement/goods-receipts', { body: { purchaseOrderId, stockLocationId, lines: [{ purchaseOrderLineId: line.id, quantity: parsed }] } }))
    },
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ['goods-receipts'] }) },
  })

  return <>
    <SectionHeader title="Goods Receipts" description="Create a partial receipt only from an approved Purchase Order, then review and explicitly post it." />
    <Stack direction={{ xs: 'column', xl: 'row' }} spacing={3} sx={{ alignItems: 'flex-start' }}>
      <Paper variant="outlined" sx={{ p: 2, flex: 1, width: '100%', overflowX: 'auto' }}>
        <LoadingOrError pending={receipts.isPending} error={receipts.error} />
        {receipts.data && <Table size="small"><TableHead><TableRow><TableCell>Receipt</TableCell><TableCell>PO</TableCell><TableCell>Supplier</TableCell><TableCell>Location</TableCell><TableCell>Status</TableCell><TableCell>Amount</TableCell><TableCell /></TableRow></TableHead><TableBody>{receipts.data.map(r => <TableRow key={r.id}><TableCell>{r.number}</TableCell><TableCell>{r.purchaseOrderNumberSnapshot}</TableCell><TableCell>{r.supplierCodeSnapshot}</TableCell><TableCell>{r.stockLocationCodeSnapshot}</TableCell><TableCell><Chip size="small" label={r.status} /></TableCell><TableCell>{r.currency} {r.totalNetAmount.toLocaleString()}</TableCell><TableCell><Button component={Link} to={`/goods-receipts/${r.id}`} size="small">Open</Button></TableCell></TableRow>)}</TableBody></Table>}
      </Paper>
      <Paper variant="outlined" sx={{ p: 2, width: { xs: '100%', xl: 420 } }}>
        <Typography variant="h6">Create Partial Receipt</Typography>
        <Stack spacing={2} sx={{ mt: 2 }}>
          <FormControl><InputLabel>Approved Purchase Order</InputLabel><Select value={purchaseOrderId} label="Approved Purchase Order" onChange={e => setPurchaseOrderId(e.target.value)}>{(purchaseOrders.data ?? []).map(po => <MenuItem key={po.id} value={po.id}>{po.number} · {po.supplierCodeSnapshot}</MenuItem>)}</Select></FormControl>
          <FormControl><InputLabel>Receiving Stock Location</InputLabel><Select value={stockLocationId} label="Receiving Stock Location" onChange={e => setStockLocationId(e.target.value)}>{(locations.data ?? []).filter(l => l.isActive).map(l => <MenuItem key={l.id} value={l.id}>{l.code} · {l.name}</MenuItem>)}</Select></FormControl>
          {selectedPo.data?.lines[0] && <Alert severity="info">Receiving {selectedPo.data.lines[0].catalogItemCodeSnapshot} in {selectedPo.data.lines[0].purchaseUomCodeSnapshot}; immutable conversion {selectedPo.data.lines[0].conversionNumerator}/{selectedPo.data.lines[0].conversionDenominator} to {selectedPo.data.lines[0].baseUomCodeSnapshot}.</Alert>}
          <TextField label="Received Quantity" type="number" value={quantity} onChange={e => setQuantity(e.target.value)} slotProps={{ htmlInput: { min: 0.000001, step: 0.001 } }} />
          {create.error && <Alert severity="error">{create.error.message}</Alert>}
          {create.data && <Alert severity="success">Goods Receipt created as DRAFT. <Link to={`/goods-receipts/${create.data.id}`}>Open receipt</Link></Alert>}
          <Button variant="contained" onClick={() => create.mutate()} disabled={create.isPending}>Create Goods Receipt</Button>
        </Stack>
      </Paper>
    </Stack>
  </>
}

export function GoodsReceiptDetailPage() {
  const { goodsReceiptId = '' } = useParams()
  const queryClient = useQueryClient()
  const detail = useQuery({
    queryKey: ['goods-receipt', goodsReceiptId], enabled: !!goodsReceiptId,
    queryFn: async () => {
      const result = await apiClient.GET('/api/procurement/goods-receipts/{goodsReceiptId}', { params: { path: { goodsReceiptId } } })
      if (result.error) throw asError(result.error)
      if (!result.data) throw new Error('Goods Receipt was not returned.')
      return { receipt: result.data, etag: result.response.headers.get('etag') ?? '' }
    },
  })
  const post = useMutation({
    mutationFn: () => requireResult(apiClient.POST('/api/procurement/goods-receipts/{goodsReceiptId}/post', {
      params: { path: { goodsReceiptId } },
      headers: { 'If-Match': detail.data?.etag ?? '', 'Idempotency-Key': crypto.randomUUID() },
    })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['goods-receipt', goodsReceiptId] })
      await queryClient.invalidateQueries({ queryKey: ['goods-receipts'] })
      await queryClient.invalidateQueries({ queryKey: ['stock-positions'] })
      await queryClient.invalidateQueries({ queryKey: ['inventory-movements'] })
    },
  })
  const receipt = detail.data?.receipt
  return <>
    <SectionHeader title="Goods Receipt Detail" description="Posting is explicit, ETag-protected and idempotent. Posted receipt history is immutable." />
    <LoadingOrError pending={detail.isPending} error={detail.error} />
    {receipt && <Stack spacing={2}>
      <Paper variant="outlined" sx={{ p: 2 }}><Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between' }}><Box><Typography variant="h5">{receipt.number}</Typography><Typography color="text.secondary">PO {receipt.purchaseOrderNumberSnapshot} · {receipt.supplierCodeSnapshot} · {receipt.stockLocationCodeSnapshot}</Typography></Box><Chip label={receipt.status} color={receipt.status === 'POSTED' ? 'success' : 'default'} /></Stack></Paper>
      <Paper variant="outlined" sx={{ p: 2, overflowX: 'auto' }}><Table size="small"><TableHead><TableRow><TableCell>Item</TableCell><TableCell>Received</TableCell><TableCell>Conversion</TableCell><TableCell>Normalized Base Qty</TableCell><TableCell>Amount</TableCell></TableRow></TableHead><TableBody>{receipt.lines.map(l => <TableRow key={l.id}><TableCell>{l.catalogItemCodeSnapshot} · {l.catalogItemNameSnapshot}</TableCell><TableCell>{l.receivedQuantity} {l.purchaseUomCodeSnapshot}</TableCell><TableCell>{l.conversionNumerator}/{l.conversionDenominator}</TableCell><TableCell>{l.normalizedBaseQuantity} {l.baseUomCodeSnapshot}</TableCell><TableCell>{receipt.currency} {l.lineNetAmount.toLocaleString()}</TableCell></TableRow>)}</TableBody></Table></Paper>
      {post.error && <Alert severity="error">{post.error.message}</Alert>}
      {post.data && <Alert severity="success">Goods Receipt posted. Inventory consequence will be applied idempotently by the tenant-aware worker.</Alert>}
      {receipt.status === 'DRAFT' && <Button variant="contained" onClick={() => post.mutate()} disabled={post.isPending || !detail.data?.etag}>Post Goods Receipt</Button>}
    </Stack>}
  </>
}

export function StockPositionsPage() {
  const queryClient = useQueryClient()
  const positions = useQuery({ queryKey: ['stock-positions'], queryFn: () => requireResult(apiClient.GET('/api/inventory/stock-positions', { params: { query: { limit: 100 } } })) })
  const rebuild = useMutation({ mutationFn: () => requireResult(apiClient.POST('/api/inventory/stock-positions/rebuild', { body: { outletId: null, catalogItemId: null } })), onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ['stock-positions'] }) } })
  return <>
    <SectionHeader title="Stock Positions" description="Derived balances only. Authoritative stock truth remains the append-only Inventory Movement ledger." />
    <Stack spacing={2}>
      <Paper variant="outlined" sx={{ p: 2, overflowX: 'auto' }}><LoadingOrError pending={positions.isPending} error={positions.error} />{positions.data && <Table size="small"><TableHead><TableRow><TableCell>Item</TableCell><TableCell>Location</TableCell><TableCell>Quantity On Hand</TableCell><TableCell>Last Movement</TableCell></TableRow></TableHead><TableBody>{positions.data.map(p => <TableRow key={p.id}><TableCell>{p.catalogItemCodeSnapshot} · {p.catalogItemNameSnapshot}</TableCell><TableCell>{p.stockLocationCodeSnapshot}</TableCell><TableCell>{p.quantityOnHand} {p.baseUomCodeSnapshot}</TableCell><TableCell>{p.lastMovementOccurredAtUtc ?? '—'}</TableCell></TableRow>)}</TableBody></Table>}</Paper>
      {rebuild.error && <Alert severity="error">{rebuild.error.message}</Alert>}
      {rebuild.data && <Alert severity="success">Rebuilt {rebuild.data.positionCount} Stock Position aggregate(s) from the movement ledger.</Alert>}
      <Button variant="outlined" onClick={() => rebuild.mutate()} disabled={rebuild.isPending}>Rebuild Scoped Stock Positions</Button>
    </Stack>
  </>
}

export function InventoryMovementsPage() {
  const movements = useQuery({ queryKey: ['inventory-movements'], queryFn: () => requireResult(apiClient.GET('/api/inventory/movements', { params: { query: { limit: 100 } } })) })
  return <>
    <SectionHeader title="Inventory Movements" description="Append-only normalized stock facts with receipt, PO, item, location, event and correlation provenance." />
    <Paper variant="outlined" sx={{ p: 2, overflowX: 'auto' }}><LoadingOrError pending={movements.isPending} error={movements.error} />{movements.data && <Table size="small"><TableHead><TableRow><TableCell>Occurred</TableCell><TableCell>Type</TableCell><TableCell>Item</TableCell><TableCell>Location</TableCell><TableCell>Base Quantity</TableCell><TableCell>Goods Receipt</TableCell><TableCell>Correlation</TableCell></TableRow></TableHead><TableBody>{movements.data.map(m => <TableRow key={m.id}><TableCell>{m.occurredAtUtc}</TableCell><TableCell>{m.movementType}</TableCell><TableCell>{m.catalogItemCodeSnapshot}</TableCell><TableCell>{m.stockLocationCodeSnapshot}</TableCell><TableCell>{m.quantityBaseUom} {m.baseUomCodeSnapshot}</TableCell><TableCell>{m.goodsReceiptId}</TableCell><TableCell>{m.correlationId}</TableCell></TableRow>)}</TableBody></Table>}</Paper>
  </>
}
