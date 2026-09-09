import { Component } from '@angular/core';

interface BankCategory {
  categoryName: string;
  description: string;
  banks: { name: string; status: string; formatNote: string }[];
}

@Component({
  selector: 'app-supported-banks',
  templateUrl: './supported-banks.component.html',
  styleUrls: ['./supported-banks.component.css'],
  standalone: false
})
export class SupportedBanksComponent {
  requestSubmitted = false;
  requestedBank = '';
  requestedEmail = '';

  bankCategories: BankCategory[] = [
    {
      categoryName: 'Major Public Sector Banks',
      description: 'Standard governmental and nationalized bank statement structures being calibrated for Phase 2.',
      banks: [
        { name: 'State Bank of India (SBI)', status: 'Mapping in Progress', formatNote: 'Standard multi-column & passbook PDF' },
        { name: 'Bank of India (BOI)', status: 'Mapping in Progress', formatNote: 'Multi-page tabular statement layout' },
        { name: 'Bank of Baroda', status: 'Mapping in Progress', formatNote: 'Baroda Connect digital statement layout' },
        { name: 'Punjab National Bank (PNB)', status: 'Mapping in Progress', formatNote: 'Retail & Corporate Internet Banking' },
        { name: 'Canara Bank', status: 'Mapping in Progress', formatNote: 'e-Syndicate & Canara statement formats' },
        { name: 'Union Bank of India', status: 'Mapping in Progress', formatNote: 'Unified corporate statement layout' }
      ]
    },
    {
      categoryName: 'Major Private Sector Banks',
      description: 'High-volume commercial bank statement templates with complex narrations.',
      banks: [
        { name: 'HDFC Bank', status: 'Mapping in Progress', formatNote: 'NetBanking & email e-statement layouts' },
        { name: 'ICICI Bank', status: 'Mapping in Progress', formatNote: 'Corporate Infinity & Retail PDF formats' },
        { name: 'Axis Bank', status: 'Mapping in Progress', formatNote: 'Internet banking & current account PDF' },
        { name: 'Kotak Mahindra Bank', status: 'Production Ready', formatNote: 'Kotak-811 & standard multi-column statement' },
        { name: 'IndusInd Bank', status: 'Mapping in Progress', formatNote: 'Compact business statement format' },
        { name: 'Yes Bank', status: 'Mapping in Progress', formatNote: 'Standard tabular transaction layout' }
      ]
    },
    {
      categoryName: 'Cooperative, Regional & International Banks',
      description: 'Extensible architecture allows quick calibration for cooperative and global bank statements.',
      banks: [
        { name: 'Standard Chartered Bank', status: 'Format Calibration', formatNote: 'International corporate layout' },
        { name: 'HSBC Bank', status: 'Format Calibration', formatNote: 'Commercial banking statement PDF' },
        { name: 'State Cooperative Banks', status: 'Format Calibration', formatNote: 'Regional accounting format support' }
      ]
    }
  ];

  submitBankRequest(): void {
    if (this.requestedBank.trim()) {
      this.requestSubmitted = true;
    }
  }
}
