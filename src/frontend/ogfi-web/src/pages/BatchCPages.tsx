import { zodResolver } from '@hookform/resolvers/zod'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  Divider,
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
import { Controller, useForm } from 'react-hook-form'
import { Link, useParams } from 'react-router'
import { z } from 'zod'
import { apiClient } from '../api/client'

function asError(error: unknown): Error {
  if (error instanceof Error) return error
  if (typeof error === 'object' && error !== null && 'detail' in error) {
    return new Error(String((error as { detail?: unknown }).detail ?? 'OGFI request failed'))
  }
  return new Error('OGFI request failed')
}

async function requireResult<T>(promise: Promise<{ data?: T; error?: unknown }>): Promise<T> {
  const result = await promise
  if (result.error) throw asError(result.error)
  if (result.data === undefined) throw new Error('OGFI returned no response body.')
  return result.data
}

function SectionHeader({ title, description }: { title: string; description: string }) {
  return (
    <Box sx={{ mb: 2 }}>
      <Typography variant="h4" fontWeight={700}>{title}</Typography>
      <Typography color="text.secondary">{description}</Typography>
    </Box>
  )
}

function LoadingOrError({ pending, error }: { pending: boolean; error: Error | null }) {
  if (pending) return <Alert severity="info">Loading authoritative server state…</Alert>
  if (error) return <Alert severity="error">{error.message}</Alert>
  return null
}

export function OverviewPage() {
  const cards = [
    ['Catalog & UOM', 'Reference UOMs, tenant Catalog Items, base UOM and effective packaging conversions.'],
    ['Inventory Setup', 'Inventory Profile and Outlet-scoped Stock Location prerequisites.'],
    ['Supplier Management', 'Tenant Suppliers and effective Supplier Offers with immutable commercial snapshots.'],
    ['Purchase Orders', 'DRAFT Purchase Orders, opaque ETag concurrency and explicit submit-to-approval action.'],
  ]

  return (
    <>
      <SectionHeader title="Batch C · Purchasing Master-Data Spine" description="RI01-BL03 candidate workspace. All business truth is server-authoritative; Batch C is not accepted until G9.4 validation." />
      <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2} flexWrap="wrap" useFlexGap>
        {cards.map(([title, body]) => (
          <Card key={title} variant="outlined" sx={{ flex: '1 1 280px' }}>
            <CardContent>
              <Typography variant="h6" gutterBottom>{title}</Typography>
              <Typography variant="body2" color="text.secondary">{body}</Typography>
            </CardContent>
          </Card>
        ))}
      </Stack>
      <Alert severity="info" sx={{ mt: 3 }}>
        Authentication and tenant identity are expected from the approved server session/BFF boundary. This UI does not expose a developer tenant override or client-supplied TenantId.
      </Alert>
    </>
  )
}

const catalogSchema = z.object({
  code: z.string().trim().min(1, 'Code is required').max(60),
  name: z.string().trim().min(1, 'Name is required').max(200),
  baseUomId: z.string().uuid('Select a base UOM'),
})
type CatalogForm = z.infer<typeof catalogSchema>

export function CatalogPage() {
  const queryClient = useQueryClient()
  const [message, setMessage] = useState<string | null>(null)
  const uoms = useQuery({
    queryKey: ['uoms'],
    queryFn: () => requireResult(apiClient.GET('/api/catalog/uoms', { params: { query: { limit: 100 } } })),
  })
  const items = useQuery({
    queryKey: ['catalog-items'],
    queryFn: () => requireResult(apiClient.GET('/api/catalog/items', { params: { query: { limit: 100 } } })),
  })
  const { register, control, handleSubmit, reset, formState: { errors } } = useForm<CatalogForm>({
    resolver: zodResolver(catalogSchema),
    defaultValues: { code: '', name: '', baseUomId: '' },
  })
  const create = useMutation({
    mutationFn: async (values: CatalogForm) => requireResult(apiClient.POST('/api/catalog/items', { body: values })),
    onSuccess: async () => {
      setMessage('Catalog Item created. Server state has been refreshed.')
      reset()
      await queryClient.invalidateQueries({ queryKey: ['catalog-items'] })
    },
  })

  return (
    <>
      <SectionHeader title="Catalog" description="Tenant Catalog Items use a stable base UOM. Current master changes do not rewrite downstream commercial snapshots." />
      <Stack direction={{ xs: 'column', xl: 'row' }} spacing={3} alignItems="flex-start">
        <Paper variant="outlined" sx={{ p: 2, flex: 1, width: '100%' }}>
          <Typography variant="h6" gutterBottom>Catalog Items</Typography>
          <LoadingOrError pending={items.isPending} error={items.error} />
          {items.data && (
            <Table size="small">
              <TableHead><TableRow><TableCell>Code</TableCell><TableCell>Name</TableCell><TableCell>Base UOM</TableCell><TableCell>Status</TableCell></TableRow></TableHead>
              <TableBody>
                {items.data.map(item => (
                  <TableRow key={item.id}><TableCell>{item.code}</TableCell><TableCell>{item.name}</TableCell><TableCell>{item.baseUomCode}</TableCell><TableCell><Chip size="small" label={item.status} /></TableCell></TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Paper>

        <Paper component="form" onSubmit={handleSubmit(values => create.mutate(values))} variant="outlined" sx={{ p: 2, width: { xs: '100%', xl: 380 } }}>
          <Typography variant="h6">Create Catalog Item</Typography>
          <Stack spacing={2} sx={{ mt: 2 }}>
            <TextField label="Code" {...register('code')} error={!!errors.code} helperText={errors.code?.message} />
            <TextField label="Name" {...register('name')} error={!!errors.name} helperText={errors.name?.message} />
            <Controller control={control} name="baseUomId" render={({ field }) => (
              <FormControl error={!!errors.baseUomId}>
                <InputLabel>Base UOM</InputLabel>
                <Select {...field} label="Base UOM">
                  {(uoms.data ?? []).map(uom => <MenuItem key={uom.id} value={uom.id}>{uom.code} · {uom.name}</MenuItem>)}
                </Select>
                {errors.baseUomId && <Typography variant="caption" color="error">{errors.baseUomId.message}</Typography>}
              </FormControl>
            )} />
            {create.error && <Alert severity="error">{create.error.message}</Alert>}
            {message && <Alert severity="success">{message}</Alert>}
            <Button type="submit" variant="contained" disabled={create.isPending}>Create Item</Button>
          </Stack>
        </Paper>
      </Stack>
    </>
  )
}

const supplierSchema = z.object({
  code: z.string().trim().min(1, 'Code is required').max(60),
  name: z.string().trim().min(1, 'Name is required').max(200),
})
type SupplierForm = z.infer<typeof supplierSchema>

export function SuppliersPage() {
  const queryClient = useQueryClient()
  const suppliers = useQuery({
    queryKey: ['suppliers'],
    queryFn: () => requireResult(apiClient.GET('/api/procurement/suppliers', { params: { query: { limit: 100 } } })),
  })
  const { register, handleSubmit, reset, formState: { errors } } = useForm<SupplierForm>({ resolver: zodResolver(supplierSchema) })
  const create = useMutation({
    mutationFn: (values: SupplierForm) => requireResult(apiClient.POST('/api/procurement/suppliers', { body: values })),
    onSuccess: async () => { reset(); await queryClient.invalidateQueries({ queryKey: ['suppliers'] }) },
  })

  return (
    <>
      <SectionHeader title="Suppliers" description="Procurement-owned supplier identity. Commercial item terms are represented separately as Supplier Offers." />
      <Stack direction={{ xs: 'column', xl: 'row' }} spacing={3} alignItems="flex-start">
        <Paper variant="outlined" sx={{ p: 2, flex: 1, width: '100%' }}>
          <Typography variant="h6" gutterBottom>Supplier Register</Typography>
          <LoadingOrError pending={suppliers.isPending} error={suppliers.error} />
          {suppliers.data && <Table size="small"><TableHead><TableRow><TableCell>Code</TableCell><TableCell>Name</TableCell><TableCell>Status</TableCell></TableRow></TableHead><TableBody>{suppliers.data.map(s => <TableRow key={s.id}><TableCell>{s.code}</TableCell><TableCell>{s.name}</TableCell><TableCell>{s.status}</TableCell></TableRow>)}</TableBody></Table>}
        </Paper>
        <Paper component="form" onSubmit={handleSubmit(values => create.mutate(values))} variant="outlined" sx={{ p: 2, width: { xs: '100%', xl: 380 } }}>
          <Typography variant="h6">Create Supplier</Typography>
          <Stack spacing={2} sx={{ mt: 2 }}>
            <TextField label="Code" {...register('code')} error={!!errors.code} helperText={errors.code?.message} />
            <TextField label="Name" {...register('name')} error={!!errors.name} helperText={errors.name?.message} />
            {create.error && <Alert severity="error">{create.error.message}</Alert>}
            <Button type="submit" variant="contained" disabled={create.isPending}>Create Supplier</Button>
          </Stack>
        </Paper>
      </Stack>
    </>
  )
}

const offerSchema = z.object({
  supplierId: z.string().uuid('Select a supplier'),
  catalogItemId: z.string().uuid('Select an item'),
  purchaseUomId: z.string().uuid('Select a purchase UOM'),
  supplierItemCode: z.string().optional(),
  unitPrice: z.coerce.number().min(0),
  currency: z.string().trim().length(3, 'Use a 3-character currency code').transform(v => v.toUpperCase()),
  effectiveFromBusinessDate: z.string().min(10),
})
type OfferForm = z.infer<typeof offerSchema>

export function SupplierOffersPage() {
  const queryClient = useQueryClient()
  const suppliers = useQuery({ queryKey: ['suppliers'], queryFn: () => requireResult(apiClient.GET('/api/procurement/suppliers', { params: { query: { limit: 100 } } })) })
  const items = useQuery({ queryKey: ['catalog-items'], queryFn: () => requireResult(apiClient.GET('/api/catalog/items', { params: { query: { limit: 100 } } })) })
  const uoms = useQuery({ queryKey: ['uoms'], queryFn: () => requireResult(apiClient.GET('/api/catalog/uoms', { params: { query: { limit: 100 } } })) })
  const offers = useQuery({ queryKey: ['supplier-offers'], queryFn: () => requireResult(apiClient.GET('/api/procurement/supplier-offers', { params: { query: { limit: 100 } } })) })
  const { register, control, handleSubmit, formState: { errors } } = useForm<OfferForm>({
    resolver: zodResolver(offerSchema),
    defaultValues: { supplierId: '', catalogItemId: '', purchaseUomId: '', currency: 'PHP', effectiveFromBusinessDate: new Date().toISOString().slice(0, 10), unitPrice: 0 },
  })
  const create = useMutation({
    mutationFn: (v: OfferForm) => requireResult(apiClient.POST('/api/procurement/supplier-offers', { body: { ...v, supplierItemCode: v.supplierItemCode || null, effectiveToBusinessDate: null } })),
    onSuccess: async () => queryClient.invalidateQueries({ queryKey: ['supplier-offers'] }),
  })

  return (
    <>
      <SectionHeader title="Supplier Offers" description="Effective supplier/item/UOM commercial facts. Purchase Orders snapshot these facts instead of reinterpreting history." />
      <Stack spacing={3}>
        <Paper variant="outlined" sx={{ p: 2, overflowX: 'auto' }}>
          <LoadingOrError pending={offers.isPending} error={offers.error} />
          {offers.data && <Table size="small"><TableHead><TableRow><TableCell>Item</TableCell><TableCell>Purchase UOM</TableCell><TableCell>Conversion</TableCell><TableCell>Price</TableCell><TableCell>Effective</TableCell></TableRow></TableHead><TableBody>{offers.data.map(o => <TableRow key={o.id}><TableCell>{o.catalogItemCodeSnapshot} · {o.catalogItemNameSnapshot}</TableCell><TableCell>{o.purchaseUomCodeSnapshot}</TableCell><TableCell>{o.conversionNumerator}/{o.conversionDenominator} {o.baseUomCodeSnapshot}</TableCell><TableCell>{o.currency} {o.unitPrice.toLocaleString()}</TableCell><TableCell>{o.effectiveFromBusinessDate}</TableCell></TableRow>)}</TableBody></Table>}
        </Paper>
        <Paper component="form" onSubmit={handleSubmit(values => create.mutate(values))} variant="outlined" sx={{ p: 2 }}>
          <Typography variant="h6">Create Supplier Offer</Typography>
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ mt: 2 }} flexWrap="wrap" useFlexGap>
            <Controller control={control} name="supplierId" render={({ field }) => <FormControl sx={{ minWidth: 220 }} error={!!errors.supplierId}><InputLabel>Supplier</InputLabel><Select {...field} label="Supplier">{(suppliers.data ?? []).map(s => <MenuItem key={s.id} value={s.id}>{s.code} · {s.name}</MenuItem>)}</Select></FormControl>} />
            <Controller control={control} name="catalogItemId" render={({ field }) => <FormControl sx={{ minWidth: 220 }} error={!!errors.catalogItemId}><InputLabel>Catalog Item</InputLabel><Select {...field} label="Catalog Item">{(items.data ?? []).map(i => <MenuItem key={i.id} value={i.id}>{i.code} · {i.name}</MenuItem>)}</Select></FormControl>} />
            <Controller control={control} name="purchaseUomId" render={({ field }) => <FormControl sx={{ minWidth: 160 }} error={!!errors.purchaseUomId}><InputLabel>Purchase UOM</InputLabel><Select {...field} label="Purchase UOM">{(uoms.data ?? []).map(u => <MenuItem key={u.id} value={u.id}>{u.code}</MenuItem>)}</Select></FormControl>} />
            <TextField label="Supplier Item Code" {...register('supplierItemCode')} />
            <TextField label="Unit Price" type="number" {...register('unitPrice')} error={!!errors.unitPrice} />
            <TextField label="Currency" {...register('currency')} error={!!errors.currency} helperText={errors.currency?.message} sx={{ width: 130 }} />
            <TextField label="Effective From" type="date" InputLabelProps={{ shrink: true }} {...register('effectiveFromBusinessDate')} />
            <Button type="submit" variant="contained" disabled={create.isPending}>Create Offer</Button>
          </Stack>
          {create.error && <Alert severity="error" sx={{ mt: 2 }}>{create.error.message}</Alert>}
        </Paper>
      </Stack>
    </>
  )
}

const poSchema = z.object({
  supplierId: z.string().uuid('Select a supplier'),
  legalEntityId: z.string().uuid('Legal Entity ID must be a UUID'),
  outletId: z.string().uuid('Outlet ID must be a UUID'),
  currency: z.string().trim().length(3).transform(v => v.toUpperCase()),
  supplierOfferId: z.string().uuid('Select a Supplier Offer'),
  quantity: z.coerce.number().positive('Quantity must be positive'),
})
type PurchaseOrderForm = z.infer<typeof poSchema>

export function PurchaseOrdersPage() {
  const queryClient = useQueryClient()
  const orders = useQuery({ queryKey: ['purchase-orders'], queryFn: () => requireResult(apiClient.GET('/api/procurement/purchase-orders', { params: { query: { limit: 100 } } })) })
  const suppliers = useQuery({ queryKey: ['suppliers'], queryFn: () => requireResult(apiClient.GET('/api/procurement/suppliers', { params: { query: { limit: 100 } } })) })
  const offers = useQuery({ queryKey: ['supplier-offers'], queryFn: () => requireResult(apiClient.GET('/api/procurement/supplier-offers', { params: { query: { limit: 100 } } })) })
  const { register, control, handleSubmit, formState: { errors } } = useForm<PurchaseOrderForm>({
    resolver: zodResolver(poSchema), defaultValues: { supplierId: '', legalEntityId: '', outletId: '', currency: 'PHP', supplierOfferId: '', quantity: 1 },
  })
  const create = useMutation({
    mutationFn: (v: PurchaseOrderForm) => requireResult(apiClient.POST('/api/procurement/purchase-orders', { body: { supplierId: v.supplierId, legalEntityId: v.legalEntityId, outletId: v.outletId, currency: v.currency, lines: [{ supplierOfferId: v.supplierOfferId, quantity: v.quantity }] } })),
    onSuccess: async () => queryClient.invalidateQueries({ queryKey: ['purchase-orders'] }),
  })

  return (
    <>
      <SectionHeader title="Purchase Orders" description="Procurement-owned DRAFT documents. Submit is an explicit command and creates the approval-start outbox atomically." />
      <Stack spacing={3}>
        <Paper variant="outlined" sx={{ p: 2, overflowX: 'auto' }}>
          <LoadingOrError pending={orders.isPending} error={orders.error} />
          {orders.data && <Table size="small"><TableHead><TableRow><TableCell>PO</TableCell><TableCell>Supplier</TableCell><TableCell>Business Date</TableCell><TableCell>Total</TableCell><TableCell>Status</TableCell></TableRow></TableHead><TableBody>{orders.data.map(po => <TableRow key={po.id}><TableCell><Button component={Link} to={`/purchase-orders/${po.id}`} size="small">{po.number}</Button></TableCell><TableCell>{po.supplierNameSnapshot}</TableCell><TableCell>{po.businessDate}</TableCell><TableCell>{po.currency} {po.totalNetAmount.toLocaleString()}</TableCell><TableCell><Chip size="small" label={po.status} /></TableCell></TableRow>)}</TableBody></Table>}
        </Paper>
        <Paper component="form" onSubmit={handleSubmit(values => create.mutate(values))} variant="outlined" sx={{ p: 2 }}>
          <Typography variant="h6">Create Draft Purchase Order</Typography>
          <Typography variant="body2" color="text.secondary">Legal Entity and Outlet are validated against the authenticated user's organization scope on the server.</Typography>
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} flexWrap="wrap" useFlexGap sx={{ mt: 2 }}>
            <Controller control={control} name="supplierId" render={({ field }) => <FormControl sx={{ minWidth: 220 }} error={!!errors.supplierId}><InputLabel>Supplier</InputLabel><Select {...field} label="Supplier">{(suppliers.data ?? []).map(s => <MenuItem key={s.id} value={s.id}>{s.code} · {s.name}</MenuItem>)}</Select></FormControl>} />
            <Controller control={control} name="supplierOfferId" render={({ field }) => <FormControl sx={{ minWidth: 260 }} error={!!errors.supplierOfferId}><InputLabel>Supplier Offer</InputLabel><Select {...field} label="Supplier Offer">{(offers.data ?? []).map(o => <MenuItem key={o.id} value={o.id}>{o.catalogItemCodeSnapshot} · {o.currency} {o.unitPrice}</MenuItem>)}</Select></FormControl>} />
            <TextField label="Legal Entity ID" {...register('legalEntityId')} error={!!errors.legalEntityId} helperText={errors.legalEntityId?.message} sx={{ minWidth: 300 }} />
            <TextField label="Outlet ID" {...register('outletId')} error={!!errors.outletId} helperText={errors.outletId?.message} sx={{ minWidth: 300 }} />
            <TextField label="Quantity" type="number" {...register('quantity')} error={!!errors.quantity} />
            <TextField label="Currency" {...register('currency')} sx={{ width: 120 }} />
            <Button type="submit" variant="contained" disabled={create.isPending}>Create Draft</Button>
          </Stack>
          {create.error && <Alert severity="error" sx={{ mt: 2 }}>{create.error.message}</Alert>}
        </Paper>
      </Stack>
    </>
  )
}

export function PurchaseOrderDetailPage() {
  const { purchaseOrderId = '' } = useParams()
  const queryClient = useQueryClient()
  const order = useQuery({
    queryKey: ['purchase-order', purchaseOrderId],
    enabled: !!purchaseOrderId,
    queryFn: async () => {
      const result = await apiClient.GET('/api/procurement/purchase-orders/{purchaseOrderId}', { params: { path: { purchaseOrderId } } })
      if (result.error) throw asError(result.error)
      if (!result.data) throw new Error('Purchase Order not found.')
      return { data: result.data, etag: result.response.headers.get('ETag') }
    },
  })
  const submit = useMutation({
    mutationFn: async () => {
      if (!order.data?.etag) throw new Error('The Purchase Order does not have a current ETag. Refresh first.')
      const result = await apiClient.POST('/api/procurement/purchase-orders/{purchaseOrderId}/submit', {
        params: { path: { purchaseOrderId } },
        headers: { 'If-Match': order.data.etag },
      })
      if (result.error) throw asError(result.error)
      return result.data
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['purchase-order', purchaseOrderId] })
      await queryClient.invalidateQueries({ queryKey: ['purchase-orders'] })
    },
  })
  const po = order.data?.data

  return (
    <>
      <SectionHeader title={po?.number ?? 'Purchase Order'} description="Detail view uses the server ETag as the only concurrency contract. No client-side status mutation is authoritative." />
      <LoadingOrError pending={order.isPending} error={order.error} />
      {po && <Paper variant="outlined" sx={{ p: 3 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} justifyContent="space-between">
          <Box><Typography variant="h5">{po.supplierNameSnapshot}</Typography><Typography color="text.secondary">{po.businessDate} · {po.currency}</Typography></Box>
          <Stack direction="row" spacing={1}><Chip label={po.status} /><Button variant="contained" disabled={po.status !== 'DRAFT' || submit.isPending} onClick={() => submit.mutate()}>Submit for Approval</Button></Stack>
        </Stack>
        {submit.error && <Alert severity="error" sx={{ mt: 2 }}>{submit.error.message}</Alert>}
        <Divider sx={{ my: 2 }} />
        <Table size="small"><TableHead><TableRow><TableCell>Item</TableCell><TableCell>Qty</TableCell><TableCell>UOM</TableCell><TableCell>Unit Price</TableCell><TableCell>Line Total</TableCell></TableRow></TableHead><TableBody>{po.lines.map(line => <TableRow key={line.id}><TableCell>{line.catalogItemCodeSnapshot} · {line.catalogItemNameSnapshot}</TableCell><TableCell>{line.orderQuantity}</TableCell><TableCell>{line.purchaseUomCodeSnapshot}</TableCell><TableCell>{po.currency} {line.unitPrice.toLocaleString()}</TableCell><TableCell>{po.currency} {line.lineNetAmount.toLocaleString()}</TableCell></TableRow>)}</TableBody></Table>
        <Typography variant="h6" align="right" sx={{ mt: 2 }}>Total: {po.currency} {po.totalNetAmount.toLocaleString()}</Typography>
      </Paper>}
    </>
  )
}

const inventoryProfileSchema = z.object({ catalogItemId: z.string().uuid('Select a Catalog Item') })
const stockLocationSchema = z.object({ outletId: z.string().uuid('Outlet ID must be a UUID'), code: z.string().trim().min(1), name: z.string().trim().min(1) })

export function InventorySetupPage() {
  const queryClient = useQueryClient()
  const profiles = useQuery({ queryKey: ['inventory-profiles'], queryFn: () => requireResult(apiClient.GET('/api/inventory/profiles', { params: { query: { limit: 100 } } })) })
  const locations = useQuery({ queryKey: ['stock-locations'], queryFn: () => requireResult(apiClient.GET('/api/inventory/stock-locations', { params: { query: { limit: 100 } } })) })
  const items = useQuery({ queryKey: ['catalog-items'], queryFn: () => requireResult(apiClient.GET('/api/catalog/items', { params: { query: { limit: 100 } } })) })
  const profileForm = useForm<z.infer<typeof inventoryProfileSchema>>({ resolver: zodResolver(inventoryProfileSchema), defaultValues: { catalogItemId: '' } })
  const locationForm = useForm<z.infer<typeof stockLocationSchema>>({ resolver: zodResolver(stockLocationSchema), defaultValues: { outletId: '', code: '', name: '' } })
  const createProfile = useMutation({ mutationFn: (v: z.infer<typeof inventoryProfileSchema>) => requireResult(apiClient.POST('/api/inventory/profiles', { body: v })), onSuccess: async () => queryClient.invalidateQueries({ queryKey: ['inventory-profiles'] }) })
  const createLocation = useMutation({ mutationFn: (v: z.infer<typeof stockLocationSchema>) => requireResult(apiClient.POST('/api/inventory/stock-locations', { body: v })), onSuccess: async () => queryClient.invalidateQueries({ queryKey: ['stock-locations'] }) })

  return (
    <>
      <SectionHeader title="Inventory Setup" description="Batch C establishes stocked-item profiles and organization-scoped stock locations; no stock movement is posted in this batch." />
      <Stack direction={{ xs: 'column', xl: 'row' }} spacing={3} alignItems="flex-start">
        <Stack spacing={3} sx={{ flex: 1, width: '100%' }}>
          <Paper variant="outlined" sx={{ p: 2 }}><Typography variant="h6">Inventory Profiles</Typography><LoadingOrError pending={profiles.isPending} error={profiles.error} />{profiles.data && <Table size="small"><TableHead><TableRow><TableCell>Catalog Item</TableCell><TableCell>Base UOM</TableCell><TableCell>Negative Stock</TableCell></TableRow></TableHead><TableBody>{profiles.data.map(p => <TableRow key={p.id}><TableCell>{p.catalogItemId}</TableCell><TableCell>{p.baseUomId}</TableCell><TableCell>{p.negativeStockAllowed ? 'Allowed' : 'Blocked'}</TableCell></TableRow>)}</TableBody></Table>}</Paper>
          <Paper variant="outlined" sx={{ p: 2 }}><Typography variant="h6">Stock Locations</Typography><LoadingOrError pending={locations.isPending} error={locations.error} />{locations.data && <Table size="small"><TableHead><TableRow><TableCell>Code</TableCell><TableCell>Name</TableCell><TableCell>Outlet</TableCell></TableRow></TableHead><TableBody>{locations.data.map(l => <TableRow key={l.id}><TableCell>{l.code}</TableCell><TableCell>{l.name}</TableCell><TableCell>{l.outletId}</TableCell></TableRow>)}</TableBody></Table>}</Paper>
        </Stack>
        <Stack spacing={3} sx={{ width: { xs: '100%', xl: 400 } }}>
          <Paper component="form" variant="outlined" sx={{ p: 2 }} onSubmit={profileForm.handleSubmit(v => createProfile.mutate(v))}>
            <Typography variant="h6">Create Inventory Profile</Typography>
            <Controller control={profileForm.control} name="catalogItemId" render={({ field }) => <FormControl fullWidth sx={{ mt: 2 }}><InputLabel>Catalog Item</InputLabel><Select {...field} label="Catalog Item">{(items.data ?? []).map(i => <MenuItem key={i.id} value={i.id}>{i.code} · {i.name}</MenuItem>)}</Select></FormControl>} />
            <Button type="submit" variant="contained" sx={{ mt: 2 }}>Create Profile</Button>
          </Paper>
          <Paper component="form" variant="outlined" sx={{ p: 2 }} onSubmit={locationForm.handleSubmit(v => createLocation.mutate(v))}>
            <Typography variant="h6">Create Stock Location</Typography>
            <Stack spacing={2} sx={{ mt: 2 }}><TextField label="Outlet ID" {...locationForm.register('outletId')} error={!!locationForm.formState.errors.outletId} helperText={locationForm.formState.errors.outletId?.message} /><TextField label="Code" {...locationForm.register('code')} /><TextField label="Name" {...locationForm.register('name')} /><Button type="submit" variant="contained">Create Location</Button></Stack>
          </Paper>
        </Stack>
      </Stack>
    </>
  )
}
