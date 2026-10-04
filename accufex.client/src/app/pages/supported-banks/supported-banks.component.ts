import { Component } from '@angular/core';
import { AuthService } from '../../services/auth.service';

export interface SupportedFormatDetail {
  formatId: string;
  name: string;
  layoutDetails: string;
}

export interface BankProfile {
  id: string;
  bankName: string;
  shortCode: string;
  category: 'Private Sector' | 'Public Sector';
  status: 'Supported';
  parserVersion: string;
  pdfType: 'Digital PDF';
  statementType: string;
  description: string;
  keyFeatures: string[];
  supportedFormats: SupportedFormatDetail[];
  unvalidatedNotes?: string;
  accentColor: string;
}

@Component({
  selector: 'app-supported-banks',
  templateUrl: './supported-banks.component.html',
  styleUrls: ['./supported-banks.component.css'],
  standalone: false
})
export class SupportedBanksComponent {
  searchQuery = '';
  selectedCategory: 'ALL' | 'Private Sector' | 'Public Sector' = 'ALL';
  expandedBankId: string | null = null;

  readonly supportedBanks: BankProfile[] = [
    {
      id: 'hdfc',
      bankName: 'HDFC Bank',
      shortCode: 'HDFC',
      category: 'Private Sector',
      status: 'Supported',
      parserVersion: 'HDFC-v1',
      pdfType: 'Digital PDF',
      statementType: 'Standard Retail & Corporate e-Statements',
      description: 'Native digital PDF parser with dynamic header interval calibration, multi-line narration reconstruction across page boundaries, and balance continuity checks.',
      keyFeatures: [
        'Dynamic column geometry calibration',
        'Multi-line narration joining across physical page boundaries (N → N+1)',
        'Invariant date formatting (dd/MM/yyyy & dd/MM/yy)',
        'Sequential running balance mathematical verification'
      ],
      supportedFormats: [
        {
          formatId: 'HDFC-v1',
          name: 'Standard Digital e-Statement',
          layoutDetails: '7-column layout (Date, Narration, Chq/Ref.No., Value Dt, Withdrawal Amt., Deposit Amt., Closing Balance)'
        }
      ],
      unvalidatedNotes: 'Scanned or photocopy PDF statements require OCR and are not supported in the standard digital-PDF parser workflow. Mobile app crops or passbook photos not supported.',
      accentColor: '#004C8F'
    },
    {
      id: 'icici',
      bankName: 'ICICI Bank',
      shortCode: 'ICICI',
      category: 'Private Sector',
      status: 'Supported',
      parserVersion: 'ICICI-v1 & ICICI-v2',
      pdfType: 'Digital PDF',
      statementType: 'Retail e-Statement & Corporate Detailed (CAA / OD / CC)',
      description: 'Dual-engine parser architecture supporting both standard retail statements and corporate multi-column overdraft / cash credit accounts with full UTR extraction.',
      keyFeatures: [
        'Dual format engines: ICICI-v1 (Retail) & ICICI-v2 (Corporate)',
        'Midpoint partitioning narration joining',
        '9-column corporate overdraft & cash credit table calibration',
        'Composite UPI, NEFT, RTGS & IMPS reference extraction'
      ],
      supportedFormats: [
        {
          formatId: 'ICICI-v1',
          name: 'Retail / NetBanking e-Statement',
          layoutDetails: 'Standard retail statement with midpoint partitioning and running balance validation'
        },
        {
          formatId: 'ICICI-v2',
          name: 'Corporate Detailed Statement (OD/CC/CAA)',
          layoutDetails: '9 columns: Sr No, Tran ID, Value Date, Transaction Date, Cheque no/RefNo, Remarks, Withdrawal (Dr), Deposit (Cr), Balance'
        }
      ],
      unvalidatedNotes: 'Photocopies, scanned images, or passbook printouts are not supported.',
      accentColor: '#F37021'
    },
    {
      id: 'axis',
      bankName: 'Axis Bank',
      shortCode: 'AXIS',
      category: 'Private Sector',
      status: 'Supported',
      parserVersion: 'AXIS-v1',
      pdfType: 'Digital PDF',
      statementType: 'Standard 9-Column Internet Banking Statement',
      description: 'Deterministic extraction of 9-column statement reports with multi-line particulars, cheque numbers, Branch SOL parsing, and DR/CR sign mapping.',
      keyFeatures: [
        '9-column tabular interval calibration',
        'Multi-line particulars and narration reconstruction',
        'Signed DR/CR indicator mapping to numerical amounts',
        'Running balance reconciliation'
      ],
      supportedFormats: [
        {
          formatId: 'AXIS-v1',
          name: 'Internet Banking Statement Report',
          layoutDetails: '9 columns: S.No, Transaction Date, Value Date, Particulars, Amount(INR), Debit/Credit, Balance(INR), Cheque Number, Branch Name(SOL)'
        }
      ],
      unvalidatedNotes: 'Custom CSV prints re-saved as PDF or unanchored layouts without SOL branch metadata are unvalidated.',
      accentColor: '#97144D'
    },
    {
      id: 'kotak',
      bankName: 'Kotak Mahindra Bank',
      shortCode: 'KOTAK',
      category: 'Private Sector',
      status: 'Supported',
      parserVersion: 'KOTAK-v1',
      pdfType: 'Digital PDF',
      statementType: 'NetBanking & Kotak e-Statement',
      description: 'Production parser supporting multi-page statements, dynamic column boundaries, composite reference extraction, and running balance continuity.',
      keyFeatures: [
        'Composite reference extraction (UPI, MB, IMPS, EBPP, TBMS, cheque)',
        'Multi-line narration reconstruction with dynamic column detection',
        'Culture-invariant Dr./Cr. amount parsing',
        'Sequential running balance continuity verification'
      ],
      supportedFormats: [
        {
          formatId: 'KOTAK-v1',
          name: 'NetBanking & Kotak e-Statement',
          layoutDetails: 'Standard multi-column layout with Date, Narration, Chq/Ref No, Withdrawal (Dr), Deposit (Cr), Balance'
        }
      ],
      unvalidatedNotes: 'Credit card statements or scanned photocopies are not supported.',
      accentColor: '#ED1C24'
    },
    {
      id: 'yes',
      bankName: 'YES BANK',
      shortCode: 'YES',
      category: 'Private Sector',
      status: 'Supported',
      parserVersion: 'YES-v1',
      pdfType: 'Digital PDF',
      statementType: 'NetBanking / Corporate e-Statement (Format 1)',
      description: 'Production parser supporting pre-anchor and post-anchor narration reconstruction, cross-page transaction continuity, and sweep-account batch validation.',
      keyFeatures: [
        'Dynamic column calibration with pre-anchor and post-anchor geometry',
        'Sweep-account transaction handling',
        'Cross-page transaction continuity across physical pages',
        'Running balance reconciliation'
      ],
      supportedFormats: [
        {
          formatId: 'YES-v1',
          name: 'NetBanking Tabular Statement (Format 1)',
          layoutDetails: 'Multi-column transaction table with strict invariant date parsing (dd-MMM-yyyy / dd/MM/yyyy)'
        }
      ],
      unvalidatedNotes: 'Corporate Format 2 is not currently validated pending verified sample provision.',
      accentColor: '#005B9F'
    },
    {
      id: 'sbi',
      bankName: 'State Bank of India',
      shortCode: 'SBI',
      category: 'Public Sector',
      status: 'Supported',
      parserVersion: 'SBI-v1',
      pdfType: 'Digital PDF',
      statementType: 'Internet Banking (INB) e-Statement',
      description: 'Parser calibrated for SBI Internet Banking digital PDF statements, supporting staggered Post Date and Value Date anchors, multi-line narration joining, and UTR extraction.',
      keyFeatures: [
        'Staggered Post Date and Value Date anchor grouping',
        'Multi-line narration joining',
        'Cheque & UTR reference detection',
        'Running balance continuity verification'
      ],
      supportedFormats: [
        {
          formatId: 'SBI-v1',
          name: 'INB Digital e-Statement',
          layoutDetails: 'Standard INB table with Txn Date, Value Date, Description, Ref No./Cheque No., Debit, Credit, Balance'
        }
      ],
      unvalidatedNotes: 'Physical branch passbook printouts, kiosk printouts, or scanned images are not supported.',
      accentColor: '#280071'
    },
    {
      id: 'boi',
      bankName: 'Bank of India',
      shortCode: 'BOI',
      category: 'Public Sector',
      status: 'Supported',
      parserVersion: 'BOI-v1',
      pdfType: 'Digital PDF',
      statementType: 'StarToken / Internet Banking Digital Statement',
      description: 'Multi-page tabular parser with dynamic column boundary detection, instrument and cheque number extraction, UTR parsing, and signed Dr./Cr. balance reconciliation.',
      keyFeatures: [
        'Dynamic column boundary detection',
        'Multi-line narration reconstruction',
        'Signed Dr./Cr. balance parsing',
        'Sequential running balance continuity'
      ],
      supportedFormats: [
        {
          formatId: 'BOI-v1',
          name: 'Internet Banking Statement',
          layoutDetails: 'Standard multi-column table with Date, Particulars, Cheque No, Debit, Credit, Balance, Dr/Cr flag'
        }
      ],
      unvalidatedNotes: 'Physical passbook branch printouts or scanned photocopies are not supported.',
      accentColor: '#E65100'
    },
    {
      id: 'central',
      bankName: 'Central Bank of India',
      shortCode: 'CBI',
      category: 'Public Sector',
      status: 'Supported',
      parserVersion: 'CENTRAL-v1',
      pdfType: 'Digital PDF',
      statementType: 'Core Banking Tabular Statement',
      description: 'Specialized nationalized bank statement parser with cross-subpage narration continuation, Dr balance semantics (negative decimals for CC/OD), and rate change exclusion.',
      keyFeatures: [
        'Cross-subpage narration continuation',
        'Dr balance semantics (negative decimals for CC/OD accounts)',
        'Rate change notice exclusion',
        'Strict dd/MM/yy and dd/MM/yyyy date parsing'
      ],
      supportedFormats: [
        {
          formatId: 'CENTRAL-v1',
          name: 'Core Banking Tabular Statement',
          layoutDetails: 'Standard transaction table with Date, Particulars, Debit, Credit, and signed Balance'
        }
      ],
      unvalidatedNotes: 'Handwritten passbooks, physical counter receipts, or dot-matrix branch copies are not supported.',
      accentColor: '#1E3A8A'
    },
    {
      id: 'pnb',
      bankName: 'Punjab National Bank',
      shortCode: 'PNB',
      category: 'Public Sector',
      status: 'Supported',
      parserVersion: 'PNB-v1',
      pdfType: 'Digital PDF',
      statementType: 'PNB ONE & Finacle Internet Banking Statement',
      description: 'Deterministic parser for Punjab National Bank statements, supporting PNB e-Statements, Finacle tabular statements, multi-line narration joining, cheque number extraction, and running balance continuity verification.',
      keyFeatures: [
        'Deterministic PNB statement & layout detection',
        'Multi-column interval calibration (Tran Date, Withdrawal, Deposit, Balance, Alpha, CHQ. NO., Narration, Additional Info)',
        'Signed Cr./Dr. balance parsing and continuity verification',
        'Cross-page continuation and header/footer exclusion'
      ],
      supportedFormats: [
        {
          formatId: 'PNB-v1',
          name: 'PNB ONE / Finacle Digital Statement',
          layoutDetails: 'Multi-column layout with Tran Date, Withdrawal, Deposit, Balance, Alpha, CHQ. NO., Narration, and Additional Info'
        }
      ],
      unvalidatedNotes: 'Scanned image copies, branch passbook photos, or dot-matrix counter receipts are not supported.',
      accentColor: '#A21D3C'
    },
    {
      id: 'bob',
      bankName: 'Bank of Baroda',
      shortCode: 'BOB',
      category: 'Public Sector',
      status: 'Supported',
      parserVersion: 'BOB-v1',
      pdfType: 'Digital PDF',
      statementType: 'Baroda Connect & bob World Statement',
      description: 'Deterministic parser for Bank of Baroda statements, supporting Baroda Connect NetBanking tabular statements, bob World e-Statements, multi-line narration reconstruction, instrument/cheque number extraction, and running balance continuity verification.',
      keyFeatures: [
        'Deterministic BOB statement & layout detection',
        'Multi-column interval calibration (S.No, Date, Value Date, Description, Cheque No, Withdrawal, Deposit, Balance)',
        'Signed Cr./Dr. balance parsing and continuity verification',
        'Cross-page continuation and header/footer exclusion'
      ],
      supportedFormats: [
        {
          formatId: 'BOB-v1',
          name: 'Baroda Connect / bob World Digital Statement',
          layoutDetails: 'Multi-column layout with Date, Description/Particulars, Cheque No, Withdrawal (Dr), Deposit (Cr), and Balance'
        }
      ],
      unvalidatedNotes: 'Scanned image copies, branch passbook photos, or dot-matrix counter receipts are not supported.',
      accentColor: '#F26522'
    }
  ];

  constructor(public authService: AuthService) {}

  get isAuthenticated(): boolean {
    return this.authService.isAuthenticated;
  }

  get totalBanksCount(): number {
    return this.supportedBanks.length;
  }

  get totalFormatsCount(): number {
    return this.supportedBanks.reduce((acc, bank) => acc + bank.supportedFormats.length, 0);
  }

  get filteredBanks(): BankProfile[] {
    let result = [...this.supportedBanks];

    // Filter by Sector Category
    if (this.selectedCategory !== 'ALL') {
      result = result.filter(b => b.category === this.selectedCategory);
    }

    // Filter by Search Query
    if (this.searchQuery.trim()) {
      const q = this.searchQuery.toLowerCase().trim();
      result = result.filter(b =>
        b.bankName.toLowerCase().includes(q) ||
        b.shortCode.toLowerCase().includes(q) ||
        b.parserVersion.toLowerCase().includes(q) ||
        b.statementType.toLowerCase().includes(q) ||
        b.category.toLowerCase().includes(q) ||
        b.description.toLowerCase().includes(q) ||
        b.keyFeatures.some(feat => feat.toLowerCase().includes(q)) ||
        b.supportedFormats.some(f => f.formatId.toLowerCase().includes(q) || f.name.toLowerCase().includes(q) || f.layoutDetails.toLowerCase().includes(q))
      );
    }

    return result;
  }

  toggleBankDetails(bankId: string): void {
    this.expandedBankId = this.expandedBankId === bankId ? null : bankId;
  }

  setCategory(category: 'ALL' | 'Private Sector' | 'Public Sector'): void {
    this.selectedCategory = category;
  }

  clearSearch(): void {
    this.searchQuery = '';
    this.selectedCategory = 'ALL';
  }
}
